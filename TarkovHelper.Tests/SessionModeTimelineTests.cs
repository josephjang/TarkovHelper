using System.IO;
using TarkovHelper.Models;
using TarkovHelper.Services.Eft;

namespace TarkovHelper.Tests;

/// <summary>
/// Guards the evidence quest-log attribution rests on: the ordered Session mode transitions in
/// one EFT session folder, and the "which mode was running at time T" lookup over them.
/// See fix-profile-data-attribution.spec.md.
/// </summary>
public sealed class SessionModeTimelineTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "tarkovhelper-timeline-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string NewSessionFolder(params string[] applicationLogLines)
    {
        var folder = Path.Combine(_root, "log_2026.08.11_09-00-00_1.1.0.46657");
        Directory.CreateDirectory(folder);
        File.WriteAllLines(
            Path.Combine(folder, "2026-08-11_09-00-00 1.1.0.46657 application.log"),
            applicationLogLines);
        return folder;
    }

    private static string ModeLine(string time, string token)
        => $"2026-08-11 {time} 123|1.1.0.46657|Info|application|Session mode: {token}";

    [Fact]
    public void A_folder_with_no_application_log_yields_an_empty_timeline()
    {
        var folder = Path.Combine(_root, "empty-session");
        Directory.CreateDirectory(folder);

        var timeline = SessionModeTimeline.Build(folder);

        Assert.Empty(timeline.Entries);
        Assert.Null(timeline.Resolve(new DateTime(2026, 8, 11, 9, 30, 0)));
    }

    [Fact]
    public void A_missing_folder_yields_an_empty_timeline_rather_than_throwing()
    {
        var timeline = SessionModeTimeline.Build(Path.Combine(_root, "does-not-exist"));

        Assert.Empty(timeline.Entries);
        Assert.Null(timeline.Resolve(DateTime.Now));
    }

    [Fact]
    public void An_application_log_without_a_session_mode_line_yields_no_entries()
    {
        var folder = NewSessionFolder(
            "2026-08-11 09:00:00.000 123|1.1.0.46657|Info|application|Init: pstrGameVersion:live",
            "2026-08-11 09:00:01.000 123|1.1.0.46657|Info|application|scene preset path:maps/woods_preset.bundle");

        var timeline = SessionModeTimeline.Build(folder);

        Assert.Empty(timeline.Entries);
    }

    [Fact]
    public void A_single_transition_is_read_with_its_own_timestamp()
    {
        var folder = NewSessionFolder(ModeLine("09:00:05.123", "Pve"));

        var timeline = SessionModeTimeline.Build(folder);

        var entry = Assert.Single(timeline.Entries);
        Assert.Equal(new DateTime(2026, 8, 11, 9, 0, 5, 123), entry.At);
        Assert.Equal(SessionProfileHint.PveZone, entry.Hint);
    }

    // The measured capture: four transitions inside five minutes in one folder. This is why
    // attribution is per-timestamp and not per-folder.
    [Fact]
    public void The_measured_four_transition_capture_is_read_in_order()
    {
        var folder = NewSessionFolder(
            ModeLine("09:00:00.000", "Pve"),
            "2026-08-11 09:01:00.000 123|1.1.0.46657|Info|application|noise between transitions",
            ModeLine("09:01:30.000", "PvpSeason"),
            ModeLine("09:03:00.000", "Regular"),
            ModeLine("09:04:45.000", "Pve"));

        var timeline = SessionModeTimeline.Build(folder);

        Assert.Equal(
            new[]
            {
                SessionProfileHint.PveZone,
                SessionProfileHint.PvpSeason,
                SessionProfileHint.PvpZone,
                SessionProfileHint.PveZone,
            },
            timeline.Entries.Select(e => e.Hint).ToArray());
        Assert.Equal(
            new[]
            {
                new DateTime(2026, 8, 11, 9, 0, 0),
                new DateTime(2026, 8, 11, 9, 1, 30),
                new DateTime(2026, 8, 11, 9, 3, 0),
                new DateTime(2026, 8, 11, 9, 4, 45),
            },
            timeline.Entries.Select(e => e.At).ToArray());
    }

    [Fact]
    public void An_event_before_the_first_transition_resolves_to_nothing()
    {
        var folder = NewSessionFolder(ModeLine("09:00:00.000", "Pve"));

        var timeline = SessionModeTimeline.Build(folder);

        // One tick before the first marker there is no evidence at all, and guessing here is
        // the defect this change exists to stop.
        Assert.Null(timeline.Resolve(new DateTime(2026, 8, 11, 9, 0, 0).AddTicks(-1)));
    }

    [Theory]
    // Exactly at the transition belongs to the new side.
    [InlineData("09:03:00.000", AppProfile.PvpZone)]
    // Just before it still belongs to the previous side.
    [InlineData("09:02:59.999", AppProfile.PvpSeason)]
    // Just after, unambiguously the new side.
    [InlineData("09:03:00.001", AppProfile.PvpZone)]
    // After the last transition, the last mode holds for the rest of the session.
    [InlineData("23:59:59.999", AppProfile.PveZone)]
    public void Events_resolve_to_the_side_of_the_transition_they_fall_on(string time, AppProfile expected)
    {
        var folder = NewSessionFolder(
            ModeLine("09:00:00.000", "Pve"),
            ModeLine("09:01:30.000", "PvpSeason"),
            ModeLine("09:03:00.000", "Regular"),
            ModeLine("09:04:45.000", "Pve"));

        var timeline = SessionModeTimeline.Build(folder);

        Assert.Equal(expected, timeline.Resolve(DateTime.Parse($"2026-08-11 {time}")));
    }

    // A transition line with no readable timestamp cannot be ordered against a quest event.
    // Stamping it "now" would sort it after every historical event and re-attribute the session.
    [Fact]
    public void A_transition_without_a_timestamp_is_dropped()
    {
        var folder = NewSessionFolder(
            ModeLine("09:00:00.000", "Pve"),
            "Session mode: PvpSeason");

        var timeline = SessionModeTimeline.Build(folder);

        var entry = Assert.Single(timeline.Entries);
        Assert.Equal(SessionProfileHint.PveZone, entry.Hint);
    }

    [Fact]
    public void Refresh_picks_up_transitions_appended_after_the_first_read()
    {
        var folder = NewSessionFolder(ModeLine("09:00:00.000", "Pve"));
        var logPath = Directory.GetFiles(folder, "*application*.log").Single();

        var timeline = SessionModeTimeline.Build(folder);
        Assert.Single(timeline.Entries);

        File.AppendAllText(logPath, ModeLine("09:10:00.000", "PvpSeason") + Environment.NewLine);
        timeline.Refresh();

        Assert.Equal(2, timeline.Entries.Count);
        Assert.Equal(AppProfile.PvpSeason, timeline.Resolve(new DateTime(2026, 8, 11, 9, 10, 1)));
    }

    // EFT flushes on buffer boundaries, not line boundaries, so a read routinely lands mid-line.
    // A truncated "Session mode: Pvp" would match the anchored pattern and misclassify a
    // seasonal session as permanent PvP, so a partial tail must be held back until it completes.
    [Fact]
    public void A_partially_written_transition_is_not_read_until_it_completes()
    {
        var folder = NewSessionFolder(ModeLine("09:00:00.000", "Pve"));
        var logPath = Directory.GetFiles(folder, "*application*.log").Single();

        var timeline = SessionModeTimeline.Build(folder);
        File.AppendAllText(logPath, "2026-08-11 09:10:00.000 123|1.1.0.46657|Info|application|Session mode: Pvp");
        timeline.Refresh();

        Assert.Single(timeline.Entries);

        File.AppendAllText(logPath, "Season" + Environment.NewLine);
        timeline.Refresh();

        Assert.Equal(SessionProfileHint.PvpSeason, timeline.Entries[^1].Hint);
    }

    // A rotated log whose new content is shorter than the old resume offset must be re-read from
    // the start, not skipped: holding the stale offset would lose every transition in it.
    [Fact]
    public void A_truncated_log_is_re_read_from_the_start()
    {
        var folder = NewSessionFolder(
            ModeLine("09:00:00.000", "Pve"),
            ModeLine("09:01:00.000", "PvpSeason"),
            ModeLine("09:02:00.000", "Regular"));
        var logPath = Directory.GetFiles(folder, "*application*.log").Single();

        var timeline = SessionModeTimeline.Build(folder);
        Assert.Equal(3, timeline.Entries.Count);

        File.WriteAllLines(logPath, new[] { ModeLine("10:00:00.000", "Pve") });
        timeline.Refresh();

        Assert.Equal(SessionProfileHint.PveZone, timeline.Entries[^1].Hint);
        Assert.Equal(new DateTime(2026, 8, 11, 10, 0, 0), timeline.Entries[^1].At);
    }

    // Several application logs in one folder are merged chronologically rather than concatenated
    // in name order.
    [Fact]
    public void Several_application_logs_in_one_folder_merge_chronologically()
    {
        var folder = Path.Combine(_root, "multi-log-session");
        Directory.CreateDirectory(folder);
        File.WriteAllLines(Path.Combine(folder, "b application.log"), new[] { ModeLine("09:00:00.000", "Pve") });
        File.WriteAllLines(Path.Combine(folder, "a application.log"), new[] { ModeLine("09:05:00.000", "PvpSeason") });

        var timeline = SessionModeTimeline.Build(folder);

        Assert.Equal(
            new[] { SessionProfileHint.PveZone, SessionProfileHint.PvpSeason },
            timeline.Entries.Select(e => e.Hint).ToArray());
        Assert.Equal(AppProfile.PveZone, timeline.Resolve(new DateTime(2026, 8, 11, 9, 1, 0)));
        Assert.Equal(AppProfile.PvpSeason, timeline.Resolve(new DateTime(2026, 8, 11, 9, 6, 0)));
    }
}
