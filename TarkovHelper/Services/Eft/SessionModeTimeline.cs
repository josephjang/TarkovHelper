using System.IO;
using TarkovHelper.Models;

namespace TarkovHelper.Services.Eft;

/// <summary>
/// The ordered <c>Session mode</c> transitions recorded in one EFT session folder, and the
/// lookup that answers "which game mode was running at time T".
/// <para>
/// This is the evidence quest-log attribution rests on. A session folder is NOT single-mode:
/// a capture recorded in <c>docs/eft-1-1-profile-selection-log-analysis.md</c> and re-measured
/// for this change shows four transitions in one folder within five minutes (Pve, PvpSeason,
/// Regular, Pve). Folder-level attribution would therefore misfile events; the lookup has to
/// be by timestamp.
/// </para>
/// <para>
/// Not thread-safe. Each consumer owns its instance: the sync path builds one per folder and
/// discards it, and the live path keeps one per folder behind its own lock.
/// </para>
/// </summary>
internal sealed class SessionModeTimeline
{
    /// <summary>
    /// The application logs a session folder writes its <c>Session mode</c> lines to. The leading
    /// wildcard is required: EFT names them <c>&lt;date&gt;_&lt;time&gt;_&lt;version&gt;
    /// application.log</c>, so a pattern anchored at "application" matches nothing at all — the
    /// same pattern <see cref="EftRaidEventService"/> uses.
    /// </summary>
    private const string ApplicationLogPattern = "*application*.log";

    private readonly string _sessionFolder;

    // Resume offsets per file, so Refresh on a growing log re-reads only what was appended.
    // Keyed by full path: a folder can hold more than one application log, and a new one can
    // appear after the first read.
    private readonly Dictionary<string, long> _positions = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<(DateTime At, SessionProfileHint Hint)> _entries = new();

    private SessionModeTimeline(string sessionFolder) => _sessionFolder = sessionFolder;

    /// <summary>The transitions read so far, oldest first.</summary>
    internal IReadOnlyList<(DateTime At, SessionProfileHint Hint)> Entries => _entries;

    /// <summary>
    /// Reads <paramref name="sessionFolder"/>'s application logs and returns the transitions
    /// they record. A folder with no application log, or one with no <c>Session mode</c> line,
    /// yields an empty timeline — which resolves everything to null rather than to a guess.
    /// </summary>
    internal static SessionModeTimeline Build(string sessionFolder)
    {
        var timeline = new SessionModeTimeline(sessionFolder);
        timeline.Refresh();
        return timeline;
    }

    /// <summary>
    /// Picks up transitions appended since the last read (and any application log that has
    /// appeared since). Cheap to call repeatedly: each file is re-read only from its resume
    /// offset, and only complete lines are consumed, so a mid-write flush cannot be parsed as
    /// a truncated token.
    /// </summary>
    internal void Refresh()
    {
        string[] files;
        try
        {
            files = Directory.Exists(_sessionFolder)
                ? Directory.GetFiles(_sessionFolder, ApplicationLogPattern, SearchOption.TopDirectoryOnly)
                : Array.Empty<string>();
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        // Ordinal by path so a folder with several application logs is read in a stable order;
        // the timestamp sort below is what actually establishes chronology across them.
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        var appended = false;
        foreach (var file in files)
        {
            appended |= ReadNewEntries(file);
        }

        // Stable sort by timestamp. Within one file the lines are already chronological, so
        // this only matters when a folder holds several logs; OrderBy is stable, so equal
        // timestamps keep file order, and an event landing exactly on a transition resolves to
        // the last one written at that instant.
        if (appended && _entries.Count > 1)
        {
            var sorted = _entries.OrderBy(e => e.At).ToList();
            _entries.Clear();
            _entries.AddRange(sorted);
        }
    }

    private bool ReadNewEntries(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            var position = _positions.GetValueOrDefault(file, 0);
            // A rotated/truncated file is re-read from the start rather than skipped: its
            // offset now points past the end, and holding it would lose every transition.
            if (stream.Length < position) position = 0;
            if (stream.Length <= position) return false;

            var (lines, nextPosition) = EftLogPatterns.FrameCompletedLines(stream, position);
            _positions[file] = nextPosition;

            var appended = false;
            foreach (var line in lines)
            {
                if (!EftLogPatterns.TryParseSessionProfile(line, out var hint)) continue;

                // A transition with no readable timestamp cannot be ordered against a quest
                // event, so it is dropped rather than stamped with "now" — which would sort it
                // after every historical event and re-attribute the whole session.
                if (!EftLogPatterns.TryExtractTimestamp(line, out var at)) continue;

                _entries.Add((at, hint));
                appended = true;
            }

            return appended;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The profile that owns an event recorded at <paramref name="at"/>: the destination of the
    /// last transition at or before that time. Returns null when there is no evidence — the
    /// event predates the first transition, the folder recorded none, or the transition names a
    /// mode this build cannot map. Callers must drop a null rather than substitute a default:
    /// guessing here is the defect this whole change exists to stop.
    /// </summary>
    /// <remarks>
    /// Both sides are local times with no offset: <c>Session mode</c> lines carry a local
    /// timestamp string, and quest events come from a Unix <c>dt</c> converted with
    /// <c>.LocalDateTime</c>. During a daylight-saving fall-back the same local hour occurs
    /// twice, so an event inside that hour can land on the wrong side of a transition that
    /// happened within it. Converting the log strings to absolute time would need the offset in
    /// force when the line was written, which the line does not carry; the exposure is recorded
    /// in fix-profile-data-attribution.spec.md rather than solved.
    /// </remarks>
    internal AppProfile? Resolve(DateTime at)
    {
        SessionProfileHint hint = SessionProfileHint.Unknown;
        var found = false;

        foreach (var entry in _entries)
        {
            if (entry.At > at) break;
            hint = entry.Hint;
            found = true;
        }

        if (!found) return null;

        return ProfileService.TryResolveDetectedProfile(hint, out var profile) ? profile : null;
    }
}
