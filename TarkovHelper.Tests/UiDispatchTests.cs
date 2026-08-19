using System.Windows.Threading;

namespace TarkovHelper.Tests;

/// <summary>
/// Contract for <see cref="UiDispatch.Post"/>, the hop background services use to reach the
/// UI thread: the work runs on the dispatcher, the caller is never held while it runs, and a
/// dispatcher that is shutting down drops the work instead of throwing. That last one is the
/// teardown race behind a crash log on exit, since a blocking Invoke from a foreign thread
/// throws once the dispatcher shuts down and nothing observes the throw inside an async void
/// timer callback.
/// </summary>
[Collection(SchedulingSensitiveCollection.Name)]
public class UiDispatchTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>A message-pumping dispatcher on its own STA thread, shut down on Dispose.</summary>
    private sealed class DispatcherThread : IDisposable
    {
        private readonly Thread _thread;

        public Dispatcher Dispatcher { get; }

        public DispatcherThread()
        {
            var ready = new ManualResetEventSlim();
            Dispatcher? dispatcher = null;

            _thread = new Thread(() =>
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = nameof(DispatcherThread),
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();

            Assert.True(ready.Wait(Patience), "the dispatcher thread never started");
            Dispatcher = dispatcher!;
        }

        public int ThreadId => _thread.ManagedThreadId;

        /// <summary>Shut down and wait for the pump to exit, as window close does.</summary>
        public void Shutdown()
        {
            Dispatcher.InvokeShutdown();
            Assert.True(_thread.Join(Patience), "the dispatcher thread never exited");
        }

        public void Dispose()
        {
            Dispatcher.InvokeShutdown();
            _thread.Join(Patience);
        }
    }

    [Fact]
    public void Post_runs_the_action_on_the_dispatcher_thread()
    {
        using var ui = new DispatcherThread();
        var ran = new ManualResetEventSlim();
        var ranOnThreadId = 0;

        var queued = UiDispatch.Post(ui.Dispatcher, () =>
        {
            ranOnThreadId = Environment.CurrentManagedThreadId;
            ran.Set();
        });

        Assert.True(queued);
        Assert.True(ran.Wait(Patience), "the posted action never ran");
        Assert.Equal(ui.ThreadId, ranOnThreadId);
        Assert.NotEqual(Environment.CurrentManagedThreadId, ranOnThreadId);
    }

    [Fact]
    public void Post_returns_before_the_action_finishes()
    {
        using var ui = new DispatcherThread();
        var started = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var finished = false;

        UiDispatch.Post(ui.Dispatcher, () =>
        {
            started.Set();
            release.Wait(Patience);
            Volatile.Write(ref finished, true);
        });

        Assert.True(started.Wait(Patience), "the posted action never started");

        // The caller is here while the dispatcher thread is still inside the action. A
        // blocking Invoke could not have got this far, which is the point: the callers are
        // background timer threads that must not be parked on the UI thread.
        Assert.False(Volatile.Read(ref finished));

        release.Set();
    }

    [Fact]
    public void Post_after_shutdown_is_dropped_instead_of_throwing()
    {
        using var ui = new DispatcherThread();
        ui.Shutdown();
        var ran = false;

        var queued = UiDispatch.Post(ui.Dispatcher, () => Volatile.Write(ref ran, true));

        Assert.False(queued);
        Assert.False(Volatile.Read(ref ran));
    }

    [Fact]
    public void Post_rejects_a_null_dispatcher_or_action()
    {
        using var ui = new DispatcherThread();

        Assert.Throws<ArgumentNullException>(() => UiDispatch.Post(null!, () => { }));
        Assert.Throws<ArgumentNullException>(() => UiDispatch.Post(ui.Dispatcher, null!));
    }
}
