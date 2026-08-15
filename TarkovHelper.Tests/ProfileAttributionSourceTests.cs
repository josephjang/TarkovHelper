using System.IO;
using System.Text.RegularExpressions;

namespace TarkovHelper.Tests;

/// <summary>
/// Source-level guards for fix-profile-data-attribution.spec.md. Reads the source tree the same
/// way FontAssetsTests and DecisionDocsTests do, via <see cref="TestRepo.Root"/>.
/// <para>
/// The defect these guard is a question asked of the wrong source, not a wrong answer computed
/// from the right one: <c>ProfileService.Instance</c> reports what the user has SELECTED, which
/// is the correct partition for hand entry and wrong for anything read from game logs. No test
/// of behaviour can prove the lookup is absent from every path (a single reintroduced call on a
/// rarely-taken branch would misfile silently), so absence is asserted structurally instead.
/// </para>
/// </summary>
public sealed class ProfileAttributionSourceTests
{
    /// <summary>
    /// The instance lookup, in every spelling that reaches the current selection. The static
    /// members (<c>ProfileService.GetProfileId</c>, <c>ProfileService.PvpProfileId</c>,
    /// <c>ProfileService.TryResolveDetectedProfile</c>) are deliberately NOT matched: they are
    /// pure maps that take their input as an argument, which is exactly the shape this change
    /// moved everything to.
    /// </summary>
    private static readonly Regex SelectionLookup = new(
        @"ProfileService\s*\.\s*Instance", RegexOptions.Compiled);

    /// <summary>
    /// One permitted selection read, anchored to the member it lives in. The member is what makes
    /// the allowance a guard rather than a quota: several of these lines are spelled identically,
    /// so a list of bare strings would let a read deleted from a constructor be paid for by a new
    /// one inside a write path.
    /// </summary>
    /// <param name="Member">Enclosing method, constructor or property accessor.</param>
    /// <param name="Line">Text the offending line must contain.</param>
    /// <param name="Reason">Why this one site may ask; read it before adding another.</param>
    private sealed record AllowedRead(string Member, string Line, string Reason);

    /// <summary>
    /// Files that carry progress writes, and the only lines in them allowed to ask which profile
    /// is selected. Each allowed line is a SELECTION or INITIAL-LOAD site, or a hand-entry write
    /// whose only possible evidence IS the selection. Everything else takes its profile as a
    /// parameter or from the snapshot it derived the change from.
    /// </summary>
    private static readonly (string RelativePath, AllowedRead[] Allowed)[] WritePathFiles =
    {
        ("TarkovHelper/Services/QuestProgressService.cs", new[]
        {
            new AllowedRead("QuestProgressService", "var profileService = ProfileService.Instance;",
                "Constructor: seeds the snapshot with the selected profile and subscribes to changes."),
            new AllowedRead("LoadProgress", "var profileService = ProfileService.Instance;",
                "Startup load: the one load with no ActiveProfileChanged to learn the profile from."),
        }),
        ("TarkovHelper/Services/LogSyncService.cs", Array.Empty<AllowedRead>()),
        ("TarkovHelper/Services/Eft/SessionModeTimeline.cs", Array.Empty<AllowedRead>()),
        ("TarkovHelper/Services/Eft/EftLogPatterns.cs", Array.Empty<AllowedRead>()),

        // The concrete store. Its own doc says none of its methods may consult ProfileService:
        // every one of them takes the partition as an argument, so this list is empty and must
        // stay empty.
        ("TarkovHelper/Services/UserDataDbService.cs", Array.Empty<AllowedRead>()),

        // The reset path (feature-complete-profile-reset.spec.md): the target profile is
        // captured by MainWindow when the confirmation opens and arrives as a parameter
        // everywhere below it, so neither the orchestrator nor the barrier may ask which
        // profile is selected. Empty and must stay empty.
        ("TarkovHelper/Services/ProfileResetService.cs", Array.Empty<AllowedRead>()),
        ("TarkovHelper/Services/TrackedUserDataWrites.cs", Array.Empty<AllowedRead>()),

        // Hideout and inventory progress is hand entry only - no log carries it - so the
        // selection IS the evidence for these writes. They are listed anyway because the
        // dangerous shape is the same one: a read taken inside a deferred body rather than
        // before it. Each site below resolves the profile before its Task.Run, which is what
        // keeps a switch from redirecting the row.
        ("TarkovHelper/Services/HideoutProgressService.cs", new[]
        {
            new AllowedRead("HideoutProgressService", "ProfileService.Instance.ActiveProfileChanged +=",
                "Constructor: subscribes to transitions, carrying the event's own profile and revision."),
            new AllowedRead("SaveSingleModule", "var profileId = ProfileService.Instance.ActiveProfileId;",
                "Hand-entered module level; resolved before the deferred save body."),
            new AllowedRead("LoadProgress", "var (profile, revision) = ProfileService.Instance.CurrentTransition;",
                "Startup load: the one load with no ActiveProfileChanged to learn the profile from."),
        }),
        ("TarkovHelper/Services/ItemInventoryService.cs", new[]
        {
            new AllowedRead("ItemInventoryService", "ProfileService.Instance.ActiveProfileChanged +=",
                "Constructor: subscribes to transitions, carrying the event's own profile and revision."),
            new AllowedRead("ScheduleSave", "_pendingSaves[itemNormalizedName] = ProfileService.Instance.ActiveProfileId;",
                "Debounced save: the profile is captured at dirty-time, not when the timer fires."),
            new AllowedRead("LoadInventory", "var (profile, revision) = ProfileService.Instance.CurrentTransition;",
                "Startup load: the one load with no ActiveProfileChanged to learn the profile from."),
        }),

        // Per-profile settings (level, scav rep, faction, prestige, DSP count, editions) are
        // hand entry only, but "hand entry" does not make the SELECTION the right partition:
        // the player edits the number that is on screen, and an automatic switch can move the
        // selection ahead of it. So the eight values live in a ProfileSettingsSnapshot that
        // carries its own profile id, writes follow that id, and the reset hook compares
        // against it like the three hooks above compare against their _loadedProfileId. The
        // three reads this file used to be allowed (HandleProfileReset, SaveProfileSetting,
        // GetProfileSetting) are gone with the code that needed them; see
        // docs/decisions/fix-profile-settings-race.spec.md.
        ("TarkovHelper/Services/SettingsService.cs", new[]
        {
            new AllowedRead("SettingsService", "ProfileService.Instance.ActiveProfileChanged +=",
                "Constructor: subscribes to transitions, carrying the event's own profile and revision."),
            new AllowedRead("LoadSettings", "var (profile, revision) = ProfileService.Instance.CurrentTransition;",
                "Startup load: the one load with no ActiveProfileChanged to learn the profile from."),
        }),

        // The legacy import. It carries writes for all four kinds of progress (quests, hideout,
        // inventory and three of the profile-scoped settings), and every one of them belongs to
        // the PvP partition by definition: the config it reads predates profiles entirely, so
        // there is nothing else it could describe. That target is named by the static
        // ProfileService.PvpProfileId, which is a constant and not a selection read, so this list
        // is empty and must stay empty - an import that consulted the selection instead would
        // silently file a returning player's whole legacy profile under PvE or the season.
        ("TarkovHelper/Services/ConfigMigrationService.cs", Array.Empty<AllowedRead>()),
    };

    [Fact]
    public void No_progress_write_path_asks_which_profile_is_selected()
    {
        var root = TestRepo.Root();

        foreach (var (relativePath, allowed) in WritePathFiles)
        {
            var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"{relativePath} is missing; update this allowlist");

            var lines = File.ReadAllLines(path);
            var remaining = new List<AllowedRead>(allowed);
            var offenders = new List<string>();

            foreach (var (line, index) in lines.Select((l, i) => (l, i)))
            {
                if (!SelectionLookup.IsMatch(line)) continue;

                var trimmed = line.Trim();
                var member = EnclosingMember(lines, index);

                // Member AND text, so a read moved out of an allowed member into a write path is
                // a new offender even when it is spelled exactly the same. Consuming the match
                // bounds the count too: a second copy inside the same member is a new lookup.
                var matchIndex = remaining.FindIndex(a =>
                    a.Member == member && trimmed.Contains(a.Line, StringComparison.Ordinal));
                if (matchIndex >= 0)
                {
                    remaining.RemoveAt(matchIndex);
                    continue;
                }

                offenders.Add($"{relativePath}:{index + 1} (in {member}): {trimmed}");
            }

            Assert.True(offenders.Count == 0,
                "A progress path reads the selected profile again. The partition must arrive as a " +
                "parameter or on the snapshot the change was derived from; see " +
                "docs/decisions/fix-profile-data-attribution.spec.md.\n" +
                string.Join("\n", offenders));

            Assert.True(remaining.Count == 0,
                $"{relativePath} no longer contains these allowlisted selection reads; " +
                "remove them from the allowlist:\n" +
                string.Join("\n", remaining.Select(a => $"{a.Member}: {a.Line}")));
        }
    }

    /// <summary>
    /// A member declaration: an access modifier, then the member name immediately before its
    /// parameter list. <c>[^=;]*?</c> keeps field and property initializers out (they carry an
    /// <c>=</c> before their parentheses), and the leading modifier keeps ordinary call sites
    /// out. Local functions are deliberately not matched - they have no access modifier - so a
    /// lookup inside one is attributed to the method that owns it.
    /// </summary>
    private static readonly Regex MemberDeclaration = new(
        @"^\s*(?:public|private|internal|protected)\b[^=;]*?(\w+)\s*\(", RegexOptions.Compiled);

    /// <summary>
    /// The member <paramref name="index"/> falls inside: the nearest declaration at or above it,
    /// or "(file scope)" when there is none.
    /// </summary>
    internal static string EnclosingMember(IReadOnlyList<string> lines, int index)
    {
        for (var i = index; i >= 0; i--)
        {
            var match = MemberDeclaration.Match(lines[i]);
            if (match.Success) return match.Groups[1].Value;
        }

        return "(file scope)";
    }

    // The anchoring is only as good as this scan, and the scan is a heuristic over text. These
    // are the shapes it has to tell apart to be worth relying on.
    [Fact]
    public void The_enclosing_member_scan_attributes_a_line_to_the_member_that_contains_it()
    {
        var source = new[]
        {
            "    public sealed class Thing",                  // 0
            "    {",                                          // 1
            "        private readonly object _sync = new();",  // 2
            "        private Thing()",                         // 3
            "        {",                                       // 4
            "            var a = ProfileService.Instance;",    // 5
            "        }",                                       // 6
            "        private async Task<int> WriteAsync(string id)", // 7
            "        {",                                       // 8
            "            var b = ProfileService.Instance;",    // 9
            "            void Local(string x)",                // 10
            "            {",                                   // 11
            "                var c = ProfileService.Instance;",// 12
            "            }",                                   // 13
            "        }",                                       // 14
        };

        Assert.Equal("Thing", EnclosingMember(source, 5));
        Assert.Equal("WriteAsync", EnclosingMember(source, 9));
        // A local function belongs to its owner, not to itself: moving a lookup into one must
        // not launder it past the allowlist.
        Assert.Equal("WriteAsync", EnclosingMember(source, 12));
        // An initializer is not a member declaration, so it cannot capture the lines below it.
        Assert.Equal("(file scope)", EnclosingMember(source, 2));
    }

    /// <summary>The source lines that belong to <paramref name="member"/>, signature included.</summary>
    private static string[] LinesOfMember(string[] lines, string member)
    {
        var body = lines
            .Select((line, index) => (line, index))
            .Where(entry => EnclosingMember(lines, entry.index) == member)
            .Select(entry => entry.line)
            .ToArray();

        Assert.True(body.Length > 0, $"no member named '{member}' was found");
        return body;
    }

    // LogSyncService raises QuestEventDetected in a tight loop over one tail read and does not
    // await the handler, while the handler reads a profile's rows before it writes them. Two
    // events for one quest (Completed then Failed) would plan against the same pre-write rows and
    // the loser's status would stick, leaving a quest the game failed recorded as Done. The
    // behavioural version of this needs a real MainWindow and dispatcher, so the gate's presence
    // is asserted structurally, the same way the selection reads above are.
    [Fact]
    public void The_live_quest_event_handler_serializes_itself_and_marshals_to_the_dispatcher()
    {
        var lines = File.ReadAllLines(Path.Combine(
            TestRepo.Root(), "TarkovHelper", "MainWindow.xaml.cs"));
        var handler = string.Join("\n", LinesOfMember(lines, "OnQuestEventDetected"));

        Assert.Contains("_questEventGate.WaitAsync()", handler);
        Assert.Contains("_questEventGate.Release()", handler);
        // Not a blocking Dispatcher.Invoke: the handler runs on a thread-pool thread and the
        // progress service raises events whose subscribers touch UI state.
        Assert.Contains("Dispatcher.InvokeAsync", handler);
        Assert.DoesNotContain("Dispatcher.Invoke(", handler);

        // A gate of any other size would not serialize.
        Assert.Contains("_questEventGate = new(1, 1)", string.Join("\n", lines));
    }

    // The event carries its owning profile precisely so consumers cannot fall back to the
    // selection. A null owner means "no evidence" and must stay nullable.
    [Fact]
    public void The_quest_log_event_owner_is_nullable()
    {
        var source = File.ReadAllText(Path.Combine(
            TestRepo.Root(), "TarkovHelper", "Models", "QuestLogEvent.cs"));

        Assert.Contains("public AppProfile? OwnerProfile { get; set; }", source);
    }

    // The configured range used to be dropped at the call site: PerformQuestSync omitted the
    // third argument, so daysRange took its default of 0 and SettingsService.SyncDaysRange
    // reached nothing (PRD R8). Asserted at the call site because that is where it was lost.
    [Fact]
    public void The_sync_entry_point_passes_the_configured_day_range()
    {
        var source = File.ReadAllText(Path.Combine(
            TestRepo.Root(), "TarkovHelper", "MainWindow.xaml.cs"));

        Assert.Matches(
            new Regex(@"SyncFromLogsAsync\(\s*logPath,\s*progress,\s*_settingsService\.SyncDaysRange\s*\)"),
            source);
    }
}
