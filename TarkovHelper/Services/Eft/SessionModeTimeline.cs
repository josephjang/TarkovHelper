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
/// Thread-safe: every public member takes the instance lock, and <see cref="Entries"/> hands
/// back a copy. The live path shares one instance per session folder across watcher callbacks
/// that run on the thread pool, so a <see cref="Refresh"/> rebuilding the list can otherwise
/// overlap a <see cref="Resolve"/> walking it. The guarantee lives here rather than in each
/// caller: the failure it prevents is an <c>InvalidOperationException</c> from the middle of a
/// live quest-event batch, which reads as "the raid was never recorded".
/// </para>
/// </summary>
internal sealed class SessionModeTimeline
{
    /// <summary>
    /// The application logs a session folder writes its <c>Session mode</c> lines to. The leading
    /// wildcard is required: EFT names them <c>&lt;date&gt;_&lt;time&gt;_&lt;version&gt;
    /// application.log</c>, so a pattern anchored at "application" matches nothing at all, the
    /// same pattern <see cref="EftRaidEventService"/> uses.
    /// </summary>
    private const string ApplicationLogPattern = "*application*.log";

    /// <summary>
    /// One transition, tagged with the file it came from so a rotated file's entries can be
    /// dropped without disturbing the other logs in the same folder.
    /// </summary>
    private readonly record struct Entry(DateTime At, SessionProfileHint Hint, string Source);

    private readonly string _sessionFolder;

    // Guards _positions and _entries. Held across the file reads too, so a Resolve can never
    // observe the list mid-rebuild.
    private readonly object _lock = new();

    // Resume offsets per file, so Refresh on a growing log re-reads only what was appended.
    // Keyed by full path: a folder can hold more than one application log, and a new one can
    // appear after the first read.
    private readonly Dictionary<string, long> _positions = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<Entry> _entries = new();

    private SessionModeTimeline(string sessionFolder) => _sessionFolder = sessionFolder;

    /// <summary>The transitions read so far, oldest first. A snapshot: later reads do not alter it.</summary>
    internal IReadOnlyList<(DateTime At, SessionProfileHint Hint)> Entries
    {
        get
        {
            lock (_lock)
            {
                return _entries.Select(e => (e.At, e.Hint)).ToArray();
            }
        }
    }

    /// <summary>
    /// Reads <paramref name="sessionFolder"/>'s application logs and returns the transitions
    /// they record. A folder with no application log, or one with no <c>Session mode</c> line,
    /// yields an empty timeline, which resolves everything to null rather than to a guess.
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

        lock (_lock)
        {
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
    }

    /// <summary>
    /// Reads one application log to exhaustion, in bounded chunks, and appends the transitions
    /// it found. Caller must hold <see cref="_lock"/>.
    /// <para>
    /// The loop is what makes the sync path correct. <see cref="EftLogPatterns.FrameCompletedLines"/>
    /// caps a single read at <see cref="EftLogPatterns.MaxReadChunkBytes"/> so no one call
    /// allocates a whole log; the live path then catches up on its next Refresh, but a sync
    /// builds a timeline once and throws it away. Stopping at the cap there would leave every
    /// transition past the first few megabytes unread, so a mid-session mode switch in a long
    /// session would resolve later quests to the mode that preceded it: the exact confident
    /// misfiling this attribution exists to stop.
    /// </para>
    /// </summary>
    private bool ReadNewEntries(string file)
    {
        // Declared outside the try so a read that fails part-way through a long log still
        // reports the entries it did append, and Refresh still re-sorts for them.
        var appended = false;

        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            var position = _positions.GetValueOrDefault(file, 0);

            // A rotated/truncated file is re-read from the start rather than skipped: its
            // offset now points past the end, and holding it would lose every transition.
            // Its already-read entries go with the offset, or the re-read duplicates them.
            if (stream.Length < position)
            {
                position = 0;
                _positions[file] = 0;
                _entries.RemoveAll(e => string.Equals(e.Source, file, StringComparison.OrdinalIgnoreCase));
            }

            while (position < stream.Length)
            {
                var (lines, nextPosition) = EftLogPatterns.FrameCompletedLines(stream, position);

                // No forward progress means the remainder is an incomplete final line (or an
                // unreadable one). Leave it for the next Refresh instead of spinning on it.
                if (nextPosition <= position) break;

                position = nextPosition;

                // Persisted per chunk rather than once at the end: whatever this chunk appends is
                // already in _entries, so an exception on a later chunk must not leave a resume
                // offset that re-reads and duplicates it.
                _positions[file] = position;

                foreach (var line in lines)
                {
                    if (!EftLogPatterns.TryParseSessionProfile(line, out var hint)) continue;

                    // A transition with no readable timestamp cannot be ordered against a quest
                    // event, so it is dropped rather than stamped with "now", which would sort it
                    // after every historical event and re-attribute the whole session.
                    if (!EftLogPatterns.TryExtractTimestamp(line, out var at)) continue;

                    _entries.Add(new Entry(at, hint, file));
                    appended = true;
                }
            }
        }
        catch (IOException)
        {
            // Left for the next Refresh: the resume offset already reflects what was consumed.
        }
        catch (UnauthorizedAccessException)
        {
        }

        return appended;
    }

    /// <summary>
    /// The profile that owns an event recorded at <paramref name="at"/>: the destination of the
    /// last transition at or before that time. Returns null when there is no evidence: the
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

        lock (_lock)
        {
            foreach (var entry in _entries)
            {
                if (entry.At > at) break;
                hint = entry.Hint;
                found = true;
            }
        }

        if (!found) return null;

        return ProfileService.TryResolveDetectedProfile(hint, out var profile) ? profile : null;
    }
}
