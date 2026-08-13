using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using TarkovHelper.Models;
using TarkovHelper.Services;
using TarkovHelper.Windows;

namespace TarkovHelper.Tests;

/// <summary>
/// Guards for the reset flow (feature-complete-profile-reset.md): the ORDER its steps run in,
/// which is what four services' doc comments lean on when they promise their caches change only
/// after the store commits, plus what happens when something in that sequence misbehaves. One
/// failing refresh hook must not cost the other services their refresh, a store transaction that
/// never returns must become a reported outcome rather than a hang, an outcome must never be built
/// in a state that renders nonsense, each outcome must reach the player under the headline that
/// tells the truth about it, and the confirmation's raid warning must not fire on a raid nobody is
/// watching any more.
/// <para>
/// The sequence is driven through <c>ProfileResetService.ResetAsync</c>'s collaborator overload,
/// which takes the four things a reset drives as arguments; the production wiring of those four is
/// <c>ProductionCollaborators</c>, and the singletons behind it are what the e2e tests exercise.
/// </para>
/// </summary>
public sealed class ProfileResetOrchestrationTests
{
    private const string ProfileId = "season";

    #region The reset sequence

    /// <summary>
    /// A fixed watermark, so the step log can assert that the store received the same moment the
    /// caller decided on rather than one it made up.
    /// </summary>
    private static readonly DateTime ResetAt = new(2026, 8, 14, 9, 30, 0, DateTimeKind.Local);

    /// <summary>Appends "barrier down" when the reset releases the write barrier.</summary>
    private sealed class BarrierRecorder : IAsyncDisposable
    {
        private readonly List<string> _steps;

        public BarrierRecorder(List<string> steps) => _steps = steps;

        public ValueTask DisposeAsync()
        {
            _steps.Add("barrier down");
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Collaborators that append every step they are asked to take to <paramref name="steps"/>,
    /// tagged with the profile they were asked about so a step driven for the wrong partition is
    /// a mismatch rather than a pass.
    /// </summary>
    /// <param name="beginReset">Replaces the barrier step; used to make the drain time out.</param>
    /// <param name="store">Runs after the store step is recorded; used to fail or abandon it.</param>
    /// <param name="hook">Runs after the hook step is recorded; used to make a hook throw.</param>
    private static ResetCollaborators Recording(
        List<string> steps,
        Func<string, Task<IAsyncDisposable>>? beginReset = null,
        Func<Task>? store = null,
        Action? hook = null)
        => new(
            BeginReset: beginReset ?? (profileId =>
            {
                steps.Add($"barrier up:{profileId}");
                return Task.FromResult<IAsyncDisposable>(new BarrierRecorder(steps));
            }),
            DiscardStagedWrites: profileId => steps.Add($"discard:{profileId}"),
            Store: (profileId, resetAt) =>
            {
                steps.Add($"store:{profileId}@{resetAt:o}");
                return store == null ? Task.CompletedTask : store();
            },
            RefreshHooks: profileId => new (string, Action)[]
            {
                ("cache", () =>
                {
                    steps.Add($"hook:{profileId}");
                    hook?.Invoke();
                }),
            });

    private static string StoreStep => $"store:{ProfileId}@{ResetAt:o}";

    // The load-bearing guarantee of the whole feature (PRD R5): memory changes only after the
    // transaction commits. QuestProgressService, HideoutProgressService, ItemInventoryService and
    // SettingsService each document their reset hook as "called strictly AFTER the store
    // transaction commits", and this is what holds the orchestrator to it.
    [Fact]
    public async Task A_reset_raises_the_barrier_discards_stores_refreshes_then_lowers_it()
    {
        var steps = new List<string>();

        var outcome = await ProfileResetService.ResetAsync(ProfileId, ResetAt, Recording(steps));

        Assert.Equal(ProfileResetStatus.Succeeded, outcome.Status);
        Assert.Equal(
            new[]
            {
                $"barrier up:{ProfileId}",
                $"discard:{ProfileId}",
                StoreStep,
                $"hook:{ProfileId}",
                "barrier down",
            },
            steps);
    }

    // PRD R5's other half: a rolled-back transaction leaves every cache exactly as it was, so no
    // refresh hook may run. A hook here would clear the pages of data still sitting in the file.
    [Fact]
    public async Task A_failing_store_refreshes_nothing_and_still_lowers_the_barrier()
    {
        var steps = new List<string>();
        var deps = Recording(
            steps, store: () => Task.FromException(new InvalidOperationException("database is locked")));

        var outcome = await ProfileResetService.ResetAsync(ProfileId, ResetAt, deps);

        Assert.Equal(ProfileResetStatus.Failed, outcome.Status);
        Assert.Equal("database is locked", outcome.Error);
        Assert.Equal(
            new[] { $"barrier up:{ProfileId}", $"discard:{ProfileId}", StoreStep, "barrier down" }, steps);
    }

    // An abandoned store is not a commit either. The orchestrator only knows the transaction did
    // NOT report success, so refreshing caches off it would show a wipe that may not have happened.
    // Reached here through a store that times out rather than a wait that does, because the real
    // budget is 30 seconds; both land in the same abandoned branch, which is the one under test.
    [Fact]
    public async Task An_abandoned_store_refreshes_nothing_and_still_lowers_the_barrier()
    {
        var steps = new List<string>();
        var deps = Recording(
            steps, store: () => Task.FromException(new TimeoutException("the write never came back")));

        var outcome = await ProfileResetService.ResetAsync(ProfileId, ResetAt, deps);

        Assert.Equal(ProfileResetStatus.Abandoned, outcome.Status);
        Assert.Equal(
            new[] { $"barrier up:{ProfileId}", $"discard:{ProfileId}", StoreStep, "barrier down" }, steps);
    }

    // A refresh hook raises a change event synchronously into UI handlers this service does not
    // own. A throwing subscriber must not strand the barrier: every later write for that profile
    // would wait on a barrier nobody will ever lower.
    [Fact]
    public async Task A_throwing_refresh_hook_still_lowers_the_barrier_and_still_succeeds()
    {
        var steps = new List<string>();
        var deps = Recording(steps, hook: () => throw new InvalidOperationException("a UI handler threw"));

        var outcome = await ProfileResetService.ResetAsync(ProfileId, ResetAt, deps);

        // The rows are gone whatever the pages now show, so this is not a failed reset.
        Assert.Equal(ProfileResetStatus.Succeeded, outcome.Status);
        Assert.Equal(
            new[]
            {
                $"barrier up:{ProfileId}",
                $"discard:{ProfileId}",
                StoreStep,
                $"hook:{ProfileId}",
                "barrier down",
            },
            steps);
    }

    // A drain that times out has removed nothing and left no barrier up, so the reset must stop
    // before it discards a single staged write.
    [Fact]
    public async Task A_drain_that_times_out_discards_nothing_and_deletes_nothing()
    {
        var steps = new List<string>();
        var deps = Recording(steps, beginReset: profileId =>
        {
            steps.Add($"barrier requested:{profileId}");
            return Task.FromException<IAsyncDisposable>(
                new TimeoutException("Timed out draining 1 in-flight write(s). Nothing was removed."));
        });

        var outcome = await ProfileResetService.ResetAsync(ProfileId, ResetAt, deps);

        Assert.Equal(ProfileResetStatus.Failed, outcome.Status);
        Assert.Contains("Nothing was removed", outcome.Error);

        // No discard, no store, and no barrier to lower: TrackedUserDataWrites lowered its own on
        // the way out, and disposing a handle this call never received is not possible.
        Assert.Equal(new[] { $"barrier requested:{ProfileId}" }, steps);
    }

    // The production wiring drives the four services the PRD names, in the order the reset runs
    // them. Building the hook list does not touch a singleton (each hook resolves its Instance
    // only when run), so the names are assertable here without standing the app up.
    [Fact]
    public void The_production_collaborators_carry_one_named_hook_per_service()
    {
        var hooks = ProfileResetService.ProductionCollaborators.RefreshHooks(ProfileId);

        Assert.Equal(
            new[] { "quest progress", "hideout progress", "item inventory", "settings" },
            hooks.Select(hook => hook.Name).ToArray());
    }

    /// <summary>
    /// Every service that declares a reset hook must be called by the orchestrator. The hooks are
    /// wired by hand in one list, so a fifth cache whose author forgets that list would be wiped
    /// on disk and left stale on screen, with nothing failing to say so.
    /// </summary>
    [Fact]
    public void Every_service_that_declares_a_reset_hook_is_called_by_the_reset_service()
    {
        var servicesDir = Path.Combine(TestRepo.Root(), "TarkovHelper", "Services");
        var orchestrator = File.ReadAllText(Path.Combine(servicesDir, "ProfileResetService.cs"));

        var declaring = new List<string>();
        foreach (var file in Directory.EnumerateFiles(servicesDir, "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("public void HandleProfileReset(string", StringComparison.Ordinal))
                {
                    continue;
                }

                declaring.Add(EnclosingClass(lines, i, file));
            }
        }

        // Proves the scan still finds anything at all: a renamed hook would otherwise turn this
        // test into a check over an empty list that passes for the wrong reason.
        Assert.Equal(
            new[]
            {
                "HideoutProgressService", "ItemInventoryService", "QuestProgressService", "SettingsService",
            },
            declaring.OrderBy(name => name, StringComparer.Ordinal).ToArray());

        var unwired = declaring
            .Where(service => !Regex.IsMatch(
                orchestrator, $@"\b{Regex.Escape(service)}\s*\.\s*Instance\s*\.\s*HandleProfileReset\s*\("))
            .ToArray();

        Assert.True(unwired.Length == 0,
            "These services handle a profile reset but ProfileResetService never calls them, so a " +
            "reset wipes their rows and leaves their caches showing the old data:\n" +
            string.Join("\n", unwired));
    }

    private static readonly Regex ClassDeclaration = new(@"\bclass\s+(\w+)", RegexOptions.Compiled);

    /// <summary>
    /// The class <paramref name="index"/> is declared in: the nearest class declaration above it
    /// indented LESS than it is. Indentation rather than the nearest declaration outright, because
    /// a nested helper type (QuestProgressService.CascadeLookups) sits at the same indent as the
    /// members of its owner and would otherwise capture every member declared after it.
    /// </summary>
    private static string EnclosingClass(IReadOnlyList<string> lines, int index, string file)
    {
        var memberIndent = Indent(lines[index]);
        for (var i = index - 1; i >= 0; i--)
        {
            var match = ClassDeclaration.Match(lines[i]);
            if (match.Success && Indent(lines[i]) < memberIndent) return match.Groups[1].Value;
        }

        throw new InvalidOperationException($"No enclosing class for line {index + 1} of {file}");
    }

    private static int Indent(string line) => line.Length - line.TrimStart().Length;

    #endregion

    #region Refresh hook isolation

    private static (string Name, Action Run) Hook(string name, List<string> ran, Exception? throws = null)
        => (name, () =>
        {
            ran.Add(name);
            if (throws != null) throw throws;
        });

    // The failure the single shared catch produced: the transaction has committed, the first
    // hook's change event reaches a UI handler that throws, and the three services after it never
    // hear about the reset at all while the dialog still reports success.
    [Fact]
    public void A_throwing_hook_does_not_cost_the_later_hooks_their_refresh()
    {
        var ran = new List<string>();
        var hooks = new[]
        {
            Hook("quest", ran, new InvalidOperationException("a UI handler threw")),
            Hook("hideout", ran),
            Hook("inventory", ran),
            Hook("settings", ran),
        };

        ProfileResetService.RunRefreshHooks(ProfileId, hooks);

        Assert.Equal(new[] { "quest", "hideout", "inventory", "settings" }, ran);
    }

    // Every hook failing is still not a failed reset: the rows are gone either way, and nothing
    // escapes to the caller that would turn a committed reset into "nothing was removed".
    [Fact]
    public void Every_hook_runs_even_when_every_hook_throws()
    {
        var ran = new List<string>();
        var hooks = new[]
        {
            Hook("quest", ran, new InvalidOperationException("one")),
            Hook("hideout", ran, new NullReferenceException("two")),
            Hook("inventory", ran, new ArgumentException("three")),
            Hook("settings", ran, new InvalidOperationException("four")),
        };

        ProfileResetService.RunRefreshHooks(ProfileId, hooks);

        Assert.Equal(new[] { "quest", "hideout", "inventory", "settings" }, ran);
    }

    [Fact]
    public void An_empty_hook_list_is_a_no_op()
        => ProfileResetService.RunRefreshHooks(ProfileId, Array.Empty<(string, Action)>());

    #endregion

    #region The store budget

    [Fact]
    public async Task A_store_transaction_that_commits_reports_success()
    {
        var outcome = await ProfileResetService.RunStoreWithinBudget(
            ProfileId, () => Task.CompletedTask, TimeSpan.FromSeconds(30));

        Assert.Equal(ProfileResetStatus.Succeeded, outcome.Status);
        Assert.True(outcome.Success);
        Assert.Null(outcome.Error);
    }

    // A wedged transaction used to hang the caller forever, and the caller is a modal that
    // refuses to close while it runs. The budget turns the hang into an outcome.
    [Fact]
    public async Task A_store_transaction_that_never_returns_becomes_an_outcome_within_the_budget()
    {
        var wedged = new TaskCompletionSource();
        var budget = TimeSpan.FromMilliseconds(200);
        var elapsed = Stopwatch.StartNew();

        var outcome = await ProfileResetService.RunStoreWithinBudget(
            ProfileId, () => wedged.Task, budget);

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10),
            $"the wedged store was waited on for {elapsed.Elapsed}, so the bound did not apply");

        // Abandoning the wait is not cancelling the transaction, so this must NOT be reported as
        // an ordinary failure: only those may claim PRD R5's "nothing was removed".
        Assert.Equal(ProfileResetStatus.Abandoned, outcome.Status);
        Assert.False(outcome.Success);

        // There is no exception behind an abandoned wait, so there is no detail line to render;
        // the dialog's own abandoned headline carries the whole message.
        Assert.Null(outcome.Error);

        wedged.SetResult();
    }

    // A rolled-back transaction reaches the player as its own message: "the database is locked"
    // must not be flattened into a generic failure.
    [Fact]
    public async Task A_failing_store_transaction_reports_its_own_message()
    {
        var outcome = await ProfileResetService.RunStoreWithinBudget(
            ProfileId,
            () => Task.FromException(new InvalidOperationException("database is locked")),
            TimeSpan.FromSeconds(30));

        Assert.Equal(ProfileResetStatus.Failed, outcome.Status);
        Assert.Equal("database is locked", outcome.Error);
    }

    // The delegate opens a connection before it returns a task, so it can throw before there is
    // any task to await. That path must be an outcome too, not an escaping exception.
    [Fact]
    public async Task A_store_call_that_throws_before_returning_a_task_is_still_an_outcome()
    {
        var outcome = await ProfileResetService.RunStoreWithinBudget(
            ProfileId,
            () => throw new InvalidOperationException("could not open the database"),
            TimeSpan.FromSeconds(30));

        Assert.Equal(ProfileResetStatus.Failed, outcome.Status);
        Assert.Equal("could not open the database", outcome.Error);
    }

    // An exception with a blank message would otherwise render an empty red line under the
    // headline, which explains nothing; the type name at least names the fault.
    [Fact]
    public async Task A_store_failure_with_a_blank_message_still_has_a_detail_to_render()
    {
        var outcome = await ProfileResetService.RunStoreWithinBudget(
            ProfileId,
            () => Task.FromException(new BlankMessageException()),
            TimeSpan.FromSeconds(30));

        Assert.Equal(ProfileResetStatus.Failed, outcome.Status);
        Assert.Equal(nameof(BlankMessageException), outcome.Error);
    }

    private sealed class BlankMessageException : Exception
    {
        public override string Message => "   ";
    }

    // Both blocking steps are bounded, and the dialog's own "give the window back" backstop is
    // sized to outlive them: if it were not, the dialog would hand back a window whose reset is
    // still legitimately running.
    [Fact]
    public void The_reset_bound_covers_both_waits_and_the_dialogs_backstop_outlives_it()
    {
        Assert.Equal(
            TrackedUserDataWrites.DefaultDrainTimeout + ProfileResetService.StoreTimeout,
            ProfileResetService.MaxDuration);
        Assert.True(ProfileResetDialog.CloseRefusalLimit > ProfileResetService.MaxDuration,
            "the dialog stops refusing to close before the reset can report its own outcome");
    }

    #endregion

    #region Outcome construction

    // The outcome used to be a two-field record anything could build, so a success carrying an
    // error message and a failure carrying none were both constructible and both rendered
    // nonsense. The factories are the only way in, and each fixes both fields together.
    [Fact]
    public void A_successful_outcome_carries_no_error()
    {
        var outcome = ProfileResetOutcome.Succeeded();

        Assert.Equal(ProfileResetStatus.Succeeded, outcome.Status);
        Assert.True(outcome.Success);
        Assert.Null(outcome.Error);
    }

    [Fact]
    public void A_failed_outcome_is_never_a_success_and_keeps_its_detail()
    {
        var outcome = ProfileResetOutcome.Failed("disk is full");

        Assert.Equal(ProfileResetStatus.Failed, outcome.Status);
        Assert.False(outcome.Success);
        Assert.Equal("disk is full", outcome.Error);
    }

    [Fact]
    public void An_abandoned_outcome_is_neither_a_success_nor_a_plain_failure()
    {
        var outcome = ProfileResetOutcome.Abandoned();

        Assert.Equal(ProfileResetStatus.Abandoned, outcome.Status);
        Assert.False(outcome.Success);
        Assert.Null(outcome.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_failure_cannot_be_built_without_a_detail_message(string blank)
        => Assert.Throws<ArgumentException>(() => ProfileResetOutcome.Failed(blank));

    [Fact]
    public void A_failure_cannot_be_built_from_a_null_exception()
        => Assert.Throws<ArgumentNullException>(() => ProfileResetOutcome.Failed((Exception)null!));

    #endregion

    #region The result headline

    private static string Headline(ProfileResetOutcome outcome)
        => ProfileResetDialog.ResultHeadline(
            TestLocalization.WithLanguage(AppLanguage.EN), outcome, AppProfile.PvpSeason);

    [Fact]
    public void The_success_headline_names_the_profile_that_was_reset()
    {
        var loc = TestLocalization.WithLanguage(AppLanguage.EN);

        Assert.Contains(
            loc.ProfileName(AppProfile.PvpSeason), Headline(ProfileResetOutcome.Succeeded()));
    }

    // PRD R5's guarantee: a rolled-back transaction removed nothing, and the headline says so.
    [Fact]
    public void The_failure_headline_states_that_nothing_was_removed()
        => Assert.Contains(
            "nothing was removed",
            Headline(ProfileResetOutcome.Failed("database is locked")),
            StringComparison.OrdinalIgnoreCase);

    // The contradiction this state exists to end: an abandoned wait rendered under the ordinary
    // failure headline told the player "nothing was removed" about a transaction that may well
    // have committed.
    [Fact]
    public void The_abandoned_headline_does_not_claim_that_nothing_was_removed()
    {
        var headline = Headline(ProfileResetOutcome.Abandoned());

        Assert.DoesNotContain("nothing was removed", headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("restart", headline, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region The raid warning gate

    // EftRaidEventService.StopMonitoring leaves the last CurrentRaid standing, so an InRaid state
    // outlives the watcher that produced it. The warning is the PRD's only mitigation for the real
    // mid-raid risk, and one that cries wolf is worse than none.
    [Theory]
    [InlineData(RaidState.InRaid)]
    [InlineData(RaidState.Matching)]
    [InlineData(RaidState.Connecting)]
    public void A_raid_state_left_over_from_a_stopped_watcher_does_not_warn(RaidState stale)
        => Assert.False(ProfileResetDialog.ShouldWarnAboutRaid(monitoring: false, stale));

    [Theory]
    [InlineData(RaidState.InRaid)]
    [InlineData(RaidState.Matching)]
    [InlineData(RaidState.Connecting)]
    public void A_live_raid_under_a_running_watcher_warns(RaidState live)
        => Assert.True(ProfileResetDialog.ShouldWarnAboutRaid(monitoring: true, live));

    [Theory]
    [InlineData(RaidState.Idle)]
    [InlineData(RaidState.Ended)]
    [InlineData(null)]
    public void A_watcher_with_no_raid_in_progress_does_not_warn(RaidState? quiet)
        => Assert.False(ProfileResetDialog.ShouldWarnAboutRaid(monitoring: true, quiet));

    #endregion
}
