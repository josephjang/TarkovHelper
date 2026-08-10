using System.IO;
using TarkovHelper.Models;
using TarkovHelper.Services;
using TarkovHelper.Services.Eft;

namespace TarkovHelper.Tests;

public class EftRaidEventParsingTests
{
    [Theory]
    [InlineData("Session mode: Pve", SessionProfileHint.PveZone, GameMode.PVE)]
    [InlineData("Session mode: Pvp", SessionProfileHint.PvpZone, GameMode.PVP)]
    [InlineData("Session mode: Regular", SessionProfileHint.PvpZone, GameMode.PVP)]
    [InlineData("2026-08-08 10:00:00.000 | Session mode: PvpSeason", SessionProfileHint.PvpSeason, GameMode.PVP)]
    public void Session_mode_tokens_map_to_profile_hint_and_game_mode(
        string line,
        SessionProfileHint expectedHint,
        GameMode expectedMode)
    {
        var parsed = EftLogPatterns.TryParseSessionProfile(line, out var hint);

        Assert.True(parsed);
        Assert.Equal(expectedHint, hint);
        Assert.Equal(expectedMode, EftRaidEventService.GameModeOf(hint));
    }

    [Theory]
    [InlineData("Session mode: PvpSeasonal")]
    [InlineData("Session mode: Pvp extra")]
    [InlineData("Session mode: Arena")]
    public void Session_mode_parser_requires_an_exact_known_token(string line)
    {
        var parsed = EftLogPatterns.TryParseSessionProfile(line, out var hint);

        Assert.False(parsed);
        Assert.Equal(SessionProfileHint.Unknown, hint);
        Assert.Equal(GameMode.Unknown, EftRaidEventService.GameModeOf(hint));
    }

    // A partially flushed line must never reach the parser as if it were complete. The
    // anchored token pattern cannot protect against this by itself: `$` is end-of-INPUT, so
    // a flush truncating at "Session mode: Pvp" matches and would misclassify a PvP Season
    // session as PvP Zone -- the exact fall-through the anchor was added to prevent.
    [Fact]
    public void Unterminated_tail_is_withheld_until_its_line_completes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"framing-{Guid.NewGuid():N}.log");
        try
        {
            File.WriteAllText(path, "2026-08-09 12:00:00.000 | first line\r\n");
            long cursor;
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var (lines, next) = EftLogPatterns.FrameCompletedLines(stream, 0);
                Assert.Equal(["2026-08-09 12:00:00.000 | first line"], lines);
                cursor = next;
            }

            // EFT flushes mid-token: the fragment happens to end at a shorter valid token.
            File.AppendAllText(path, "2026-08-09 12:00:02.000 | Session mode: Pvp");
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var (lines, next) = EftLogPatterns.FrameCompletedLines(stream, cursor);
                Assert.Empty(lines);
                Assert.Equal(cursor, next);
            }

            // The rest arrives; the line is now delivered exactly once, and whole.
            File.AppendAllText(path, "Season\r\n");
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var (lines, _) = EftLogPatterns.FrameCompletedLines(stream, cursor);
                Assert.Equal(["2026-08-09 12:00:02.000 | Session mode: PvpSeason"], lines);
                Assert.True(EftLogPatterns.TryParseSessionProfile(lines[0], out var hint));
                Assert.Equal(SessionProfileHint.PvpSeason, hint);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Completed_profile_selection_accepts_legacy_and_new_completed_lines()
    {
        const string legacyId = "0123456789abcdef01234567";
        const string completedId = "89abcdef0123456701234567";

        Assert.True(EftRaidEventService.TryParseCompletedProfileSelection(
            $"SelectProfile ProfileId:{legacyId} AccountId:1234567",
            out var parsedLegacyId,
            out var legacyAccountId));
        Assert.Equal(legacyId, parsedLegacyId);
        Assert.Equal("1234567", legacyAccountId);

        Assert.True(EftRaidEventService.TryParseCompletedProfileSelection(
            $"CompleteSelectedProfile ProfileId:{completedId} AccountId:1234567",
            out var parsedCompletedId,
            out var completedAccountId));
        Assert.Equal(completedId, parsedCompletedId);
        Assert.Equal("1234567", completedAccountId);
        Assert.NotEqual(parsedLegacyId, parsedCompletedId);
    }

    // A file with no newline at all has no complete line, so nothing may be dispatched and the
    // cursor must not move -- otherwise a single partial line would be consumed and lost.
    [Fact]
    public void A_file_with_no_complete_line_yields_nothing()
    {
        using var stream = new MemoryStream("2026-08-09 12:00:00.000 | Session mode: Pvp"u8.ToArray());

        var (lines, next) = EftLogPatterns.FrameCompletedLines(stream, 0);

        Assert.Empty(lines);
        Assert.Equal(0, next);
    }

    // Bare LF (no CR) and blank lines must both frame correctly.
    [Fact]
    public void Lf_only_and_blank_lines_are_framed()
    {
        using var stream = new MemoryStream("first\n\nsecond\n"u8.ToArray());

        var (lines, next) = EftLogPatterns.FrameCompletedLines(stream, 0);

        Assert.Equal(["first", "second"], lines);
        Assert.Equal(stream.Length, next);
    }

    // Reads are chunk-bounded so one call cannot allocate a whole multi-megabyte log. The
    // remainder must still be delivered by the following read rather than skipped.
    [Fact]
    public void A_span_larger_than_one_chunk_is_delivered_across_reads()
    {
        var line = new string('x', 1000);
        var builder = new System.Text.StringBuilder();
        var lineCount = (EftLogPatterns.MaxReadChunkBytes / (line.Length + 2)) + 50;
        for (var i = 0; i < lineCount; i++) builder.Append(line).Append("\r\n");
        var bytes = System.Text.Encoding.UTF8.GetBytes(builder.ToString());

        using var stream = new MemoryStream(bytes);
        var delivered = 0;
        long cursor = 0;
        var reads = 0;
        while (cursor < bytes.Length && reads < 10)
        {
            var (lines, next) = EftLogPatterns.FrameCompletedLines(stream, cursor);
            Assert.True(next > cursor, "framing must make forward progress");
            delivered += lines.Count;
            cursor = next;
            reads++;
        }

        Assert.Equal(lineCount, delivered);
        Assert.Equal(bytes.Length, cursor);
        Assert.True(reads > 1, "the span should have needed more than one bounded read");
    }

    [Theory]
    [InlineData("PrepareSelectedProfileLocally ProfileId:0123456789abcdef01234567 AccountId:1234567")]
    [InlineData("NotCompleteSelectedProfile ProfileId:0123456789abcdef01234567 AccountId:1234567")]
    // A profile id is exactly 24 hex characters. Anything else is not an identity this code can
    // reason about, and accepting one would persist it as the durable PMC identity.
    [InlineData("CompleteSelectedProfile ProfileId:a AccountId:1")]
    [InlineData("CompleteSelectedProfile ProfileId:0 AccountId:0")]
    [InlineData("CompleteSelectedProfile ProfileId:0123456789abcdef0123456 AccountId:1234567")]
    [InlineData("CompleteSelectedProfile ProfileId:0123456789abcdef012345678 AccountId:1234567")]
    public void Non_completed_profile_selection_is_not_published(string line)
    {
        Assert.False(EftRaidEventService.TryParseCompletedProfileSelection(
            line,
            out var profileId,
            out var accountId));
        Assert.Empty(profileId);
        Assert.Empty(accountId);
    }

    // The pattern is case-insensitive, so the same identity can arrive in either case. It is
    // normalized at this one boundary because every downstream comparison is ordinal.
    [Fact]
    public void Completed_profile_selection_normalizes_identity_case()
    {
        Assert.True(EftRaidEventService.TryParseCompletedProfileSelection(
            "CompleteSelectedProfile ProfileId:0123456789ABCDEF01234567 AccountId:1234567",
            out var profileId,
            out _));

        Assert.Equal("0123456789abcdef01234567", profileId);
    }

    // The pre-EFT-1.1 pattern tolerated any trailing content; only the leading word boundary is
    // needed to reject PrepareSelectedProfileLocally / NotCompleteSelectedProfile, so a trailing
    // separator must not make profile identity detection stop entirely.
    [Theory]
    [InlineData("CompleteSelectedProfile ProfileId:0123456789abcdef01234567 AccountId:1234567,")]
    [InlineData("CompleteSelectedProfile ProfileId:0123456789abcdef01234567 AccountId:1234567)")]
    [InlineData("CompleteSelectedProfile ProfileId:0123456789abcdef01234567 AccountId:1234567 done")]
    public void Completed_profile_selection_tolerates_a_trailing_separator(string line)
    {
        Assert.True(EftRaidEventService.TryParseCompletedProfileSelection(
            line,
            out var profileId,
            out var accountId));

        Assert.Equal("0123456789abcdef01234567", profileId);
        Assert.Equal("1234567", accountId);
    }
}
