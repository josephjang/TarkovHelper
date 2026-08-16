using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// Guards the collapsing rule behind the profile-scoped settings refresh on ItemsPage and
/// QuestListPage. SettingsService raises all seven of its changed events on every published
/// reload, and both pages consume five of them with the same full rebuild; without coalescing one
/// profile switch ran that rebuild five times.
/// <para>
/// The pages themselves are WPF UserControls whose refresh needs a loaded application and an STA
/// dispatcher, so the testable part is the coalescer with its scheduler injected.
/// </para>
/// </summary>
[Collection(SchedulingSensitiveCollection.Name)]
public sealed class RefreshCoalescerTests
{
    /// <summary>
    /// Stands in for <c>Dispatcher.InvokeAsync(..., DispatcherPriority.Background)</c>: the
    /// callback is queued, not run, until the test drains it. That is the case that matters, since
    /// the whole point is that the burst finishes arriving before the refresh runs.
    /// </summary>
    private sealed class QueueingScheduler
    {
        private readonly List<Action> _queued = new();

        public int ScheduleCount { get; private set; }

        public void Schedule(Action callback)
        {
            lock (_queued)
            {
                ScheduleCount++;
                _queued.Add(callback);
            }
        }

        /// <summary>Runs everything queued so far, in order.</summary>
        public void Drain()
        {
            Action[] pending;
            lock (_queued)
            {
                pending = _queued.ToArray();
                _queued.Clear();
            }
            foreach (var callback in pending) callback();
        }
    }

    [Fact]
    public void Burst_of_requests_produces_exactly_one_refresh()
    {
        var refreshes = 0;
        var scheduler = new QueueingScheduler();
        var coalescer = new RefreshCoalescer(() => refreshes++, scheduler.Schedule);

        // The five settings events one profile switch delivers to a page.
        for (var i = 0; i < 5; i++) coalescer.Request();

        Assert.Equal(1, scheduler.ScheduleCount);
        Assert.Equal(0, refreshes); // nothing runs until the dispatcher gets round to it

        scheduler.Drain();

        Assert.Equal(1, refreshes);
    }

    [Fact]
    public void Single_request_still_refreshes_once()
    {
        var refreshes = 0;
        var scheduler = new QueueingScheduler();
        var coalescer = new RefreshCoalescer(() => refreshes++, scheduler.Schedule);

        // A lone edit of one setting must not be swallowed by the coalescing.
        coalescer.Request();
        scheduler.Drain();

        Assert.Equal(1, refreshes);
    }

    [Fact]
    public void Request_after_the_refresh_ran_schedules_another()
    {
        var refreshes = 0;
        var scheduler = new QueueingScheduler();
        var coalescer = new RefreshCoalescer(() => refreshes++, scheduler.Schedule);

        coalescer.Request();
        coalescer.Request();
        scheduler.Drain();
        Assert.Equal(1, refreshes);

        // A later, separate change is a new burst, not a continuation of the old one.
        coalescer.Request();
        scheduler.Drain();

        Assert.Equal(2, refreshes);
        Assert.Equal(2, scheduler.ScheduleCount);
    }

    [Fact]
    public void Request_raised_while_the_refresh_runs_books_another_pass()
    {
        // The refresh reads state when it runs. A change landing mid-refresh may or may not be
        // visible to that pass, so it has to book a second one instead of being swallowed.
        var refreshes = 0;
        var scheduler = new QueueingScheduler();
        RefreshCoalescer? coalescer = null;
        var reentered = false;
        coalescer = new RefreshCoalescer(
            () =>
            {
                refreshes++;
                if (reentered) return;
                reentered = true;
                coalescer!.Request();
            },
            scheduler.Schedule);

        coalescer.Request();
        scheduler.Drain();
        Assert.Equal(1, refreshes);

        scheduler.Drain();

        Assert.Equal(2, refreshes);
    }

    [Fact]
    public void Synchronous_scheduler_refreshes_once_per_request()
    {
        // Degenerate scheduler: the callback runs inline, so no request can ever join another.
        var refreshes = 0;
        var coalescer = new RefreshCoalescer(() => refreshes++, callback => callback());

        coalescer.Request();
        coalescer.Request();
        coalescer.Request();

        Assert.Equal(3, refreshes);
    }

    [Fact]
    public void Concurrent_requests_still_schedule_only_one_refresh()
    {
        // Settings events can be raised off the UI thread, so Request is called concurrently.
        var refreshes = 0;
        var scheduler = new QueueingScheduler();
        var coalescer = new RefreshCoalescer(() => refreshes++, scheduler.Schedule);

        using var start = new ManualResetEventSlim(false);
        var threads = new Thread[16];
        for (var i = 0; i < threads.Length; i++)
        {
            threads[i] = new Thread(() =>
            {
                start.Wait();
                for (var n = 0; n < 50; n++) coalescer.Request();
            });
            threads[i].Start();
        }
        start.Set();
        foreach (var thread in threads) thread.Join();

        // Nothing was drained while the threads ran, so every request after the first joined it.
        Assert.Equal(1, scheduler.ScheduleCount);

        scheduler.Drain();

        Assert.Equal(1, refreshes);
    }

    [Fact]
    public void A_scheduler_that_throws_leaves_the_coalescer_usable()
    {
        var refreshes = 0;
        var fail = true;
        var scheduler = new QueueingScheduler();
        var coalescer = new RefreshCoalescer(
            () => refreshes++,
            callback =>
            {
                if (fail) throw new InvalidOperationException("dispatcher shut down");
                scheduler.Schedule(callback);
            });

        Assert.Throws<InvalidOperationException>(() => coalescer.Request());

        // The failed schedule will never invoke the callback, so the pending flag must have come
        // back down or every later change would be silently dropped.
        fail = false;
        coalescer.Request();
        scheduler.Drain();

        Assert.Equal(1, refreshes);
    }

    [Fact]
    public void Constructor_rejects_null_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => new RefreshCoalescer(null!, _ => { }));
        Assert.Throws<ArgumentNullException>(() => new RefreshCoalescer(() => { }, null!));
    }
}
