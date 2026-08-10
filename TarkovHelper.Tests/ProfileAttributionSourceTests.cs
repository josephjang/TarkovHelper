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
/// of behaviour can prove the lookup is absent from every path — a single reintroduced call on a
/// rarely-taken branch would misfile silently — so absence is asserted structurally instead.
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
    /// Files that carry progress writes, and the only lines in them allowed to ask which profile
    /// is selected. Each allowed line is a SELECTION or INITIAL-LOAD site: the service has to
    /// learn the starting profile from somewhere, and it has to subscribe to changes. Everything
    /// else takes its profile as a parameter or from the snapshot it derived the change from.
    /// </summary>
    private static readonly (string RelativePath, string[] AllowedContaining)[] WritePathFiles =
    {
        ("TarkovHelper/Services/QuestProgressService.cs", new[]
        {
            // Constructor: seeds the snapshot with the currently selected profile and subscribes
            // to later changes.
            "var profileService = ProfileService.Instance;",
            // Startup load: the one load with no ActiveProfileChanged to learn the profile from.
            "var profileService = ProfileService.Instance;",
        }),
        ("TarkovHelper/Services/LogSyncService.cs", Array.Empty<string>()),
        ("TarkovHelper/Services/Eft/SessionModeTimeline.cs", Array.Empty<string>()),
        ("TarkovHelper/Services/Eft/EftLogPatterns.cs", Array.Empty<string>()),
    };

    [Fact]
    public void No_progress_write_path_asks_which_profile_is_selected()
    {
        var root = TestRepo.Root();

        foreach (var (relativePath, allowed) in WritePathFiles)
        {
            var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"{relativePath} is missing; update this allowlist");

            var remaining = new List<string>(allowed);
            var offenders = new List<string>();

            foreach (var (line, number) in File.ReadAllLines(path).Select((l, i) => (l, i + 1)))
            {
                if (!SelectionLookup.IsMatch(line)) continue;

                var trimmed = line.Trim();
                var matchIndex = remaining.FindIndex(a => trimmed.Contains(a, StringComparison.Ordinal));
                if (matchIndex >= 0)
                {
                    // Consume it, so the allowlist bounds the COUNT as well as the shape: a
                    // second copy of an allowed line is still a new lookup.
                    remaining.RemoveAt(matchIndex);
                    continue;
                }

                offenders.Add($"{relativePath}:{number}: {trimmed}");
            }

            Assert.True(offenders.Count == 0,
                "A progress path reads the selected profile again. The partition must arrive as a " +
                "parameter or on the snapshot the change was derived from; see " +
                "docs/decisions/fix-profile-data-attribution.spec.md.\n" +
                string.Join("\n", offenders));

            Assert.True(remaining.Count == 0,
                $"{relativePath} no longer contains these allowlisted selection reads; " +
                $"remove them from the allowlist:\n{string.Join("\n", remaining)}");
        }
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
