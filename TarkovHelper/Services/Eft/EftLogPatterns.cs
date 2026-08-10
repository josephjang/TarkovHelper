using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using TarkovHelper.Models;

namespace TarkovHelper.Services.Eft;

/// <summary>
/// The single home for reading EFT application-log lines: the <c>Session mode</c> token,
/// the leading timestamp, and the byte framing that guarantees only complete lines reach
/// either of them.
/// <para>
/// Extracted from <see cref="EftRaidEventService"/> because a second consumer arrived —
/// <see cref="SessionModeTimeline"/>, which attributes quest-log events to the game mode of
/// the session that produced them. Writing a second <c>Session mode</c> matcher was the
/// obvious shortcut and was rejected: this pattern has already been corrected once, for a
/// prefix bug where <c>Session mode: PvpSeason</c> matched the <c>Pvp</c> alternative and
/// classified a seasonal session as permanent PvP. A second copy is a second place for that
/// class of bug to survive a fix. Nothing about the patterns changed in the move.
/// </para>
/// </summary>
internal static class EftLogPatterns
{
    // Match the complete token so PvpSeason cannot fall through to the Pvp prefix.
    private static readonly Regex SessionModeRegex = new(
        @"Session mode:\s*(Pve|PvpSeason|Pvp|Regular)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Timestamp 추출 (로그 라인 시작)
    private static readonly Regex TimestampRegex = new(
        @"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})",
        RegexOptions.Compiled);

    /// <summary>
    /// Recognizes a completed <c>Session mode</c> line and maps its token to the profile it
    /// names. Returns false — leaving <paramref name="profileHint"/> Unknown — for any other
    /// line, including a token this build does not know.
    /// </summary>
    internal static bool TryParseSessionProfile(string line, out SessionProfileHint profileHint)
    {
        var match = SessionModeRegex.Match(line);
        if (!match.Success)
        {
            profileHint = SessionProfileHint.Unknown;
            return false;
        }

        profileHint = match.Groups[1].Value.ToLowerInvariant() switch
        {
            "pve" => SessionProfileHint.PveZone,
            "pvpseason" => SessionProfileHint.PvpSeason,
            "pvp" or "regular" => SessionProfileHint.PvpZone,
            _ => SessionProfileHint.Unknown
        };
        return profileHint != SessionProfileHint.Unknown;
    }

    /// <summary>
    /// The local time a log line carries at its start, or null when the line has none.
    /// <para>
    /// Callers that need an ordering key for a timeline must distinguish "no timestamp" from
    /// a real one, which is why this is a Try- shape and <see cref="ExtractTimestamp"/> is a
    /// separate convenience: substituting <c>DateTime.Now</c> for a missing timestamp would
    /// place an undated line at the end of a historical timeline rather than dropping it.
    /// </para>
    /// </summary>
    internal static bool TryExtractTimestamp(string line, out DateTime timestamp)
    {
        var match = TimestampRegex.Match(line);
        if (match.Success && DateTime.TryParse(match.Groups[1].Value, out timestamp))
        {
            return true;
        }

        timestamp = default;
        return false;
    }

    /// <summary>
    /// The local time a log line carries, falling back to <c>DateTime.Now</c> when it has
    /// none. For live event stamping, where "when we saw it" is an acceptable stand-in.
    /// </summary>
    internal static DateTime ExtractTimestamp(string line)
        => TryExtractTimestamp(line, out var timestamp) ? timestamp : DateTime.Now;

    /// <summary>
    /// A line longer than this with no newline is dispatched unterminated rather than
    /// buffered forever, so a process that died mid-line cannot stall the tail.
    /// </summary>
    internal const int MaxUnterminatedLineBytes = 1024 * 1024;

    /// <summary>Upper bound on bytes framed per read, so one call cannot allocate a whole log.</summary>
    internal const int MaxReadChunkBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Frames the bytes appended since <paramref name="lastPosition"/> at the LAST newline and
    /// returns only complete lines, plus the position to resume from.
    /// <para>
    /// EFT keeps the log open and flushes on buffer boundaries, not line boundaries, so a read
    /// triggered by the size/last-write watcher or the 1 s poll routinely lands mid-line.
    /// <c>StreamReader.ReadLine</c> hands back such a partial tail as if it were a whole line
    /// and the stream position then sits past it, so the completing bytes are never re-read.
    /// That silently defeats the anchored token patterns: a flush truncating at
    /// <c>Session mode: Pvp</c> matches (<c>$</c> is end-of-input, not end-of-line) and
    /// misclassifies a PvP Season session as PvP Zone, while a truncation at
    /// <c>Session mode: PvpSea</c> loses the transition entirely.
    /// </para>
    /// </summary>
    internal static (List<string> Lines, long NextPosition) FrameCompletedLines(
        Stream stream, long lastPosition)
    {
        var lines = new List<string>();
        if (lastPosition < 0) lastPosition = 0;
        if (stream.Length <= lastPosition) return (lines, lastPosition);

        // Bound the buffer: a first read of an existing multi-megabyte log (or a log file the
        // watcher reports as created) would otherwise allocate the whole remainder at once.
        // Whatever is left over is picked up by the next poll.
        var pendingLength = Math.Min(stream.Length - lastPosition, MaxReadChunkBytes);
        stream.Seek(lastPosition, SeekOrigin.Begin);

        var buffer = new byte[pendingLength];
        var read = 0;
        while (read < buffer.Length)
        {
            var chunk = stream.Read(buffer, read, buffer.Length - read);
            if (chunk <= 0) break;
            read += chunk;
        }

        // A concurrent truncation can leave nothing readable even though Length said otherwise;
        // Array.LastIndexOf rejects the resulting -1 start index, so stop before asking it.
        if (read <= 0) return (lines, lastPosition);

        var lastNewline = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
        int usable;
        if (lastNewline >= 0)
        {
            usable = lastNewline + 1;
        }
        else if (read >= MaxUnterminatedLineBytes)
        {
            // No newline in an implausibly long span: give up on framing this one rather
            // than re-reading the same bytes on every poll forever.
            usable = read;
        }
        else
        {
            // Nothing complete yet — leave the whole partial tail for the next read.
            return (lines, lastPosition);
        }

        var text = new UTF8Encoding(false).GetString(buffer, 0, usable);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            if (line.Length > 0) lines.Add(line);
        }

        return (lines, lastPosition + usable);
    }
}
