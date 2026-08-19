using System.IO;
using System.Text;
using Xunit.Sdk;

namespace TarkovHelper.Tests;

/// <summary>
/// The guard on the shared byte comparison. What is asserted here is the diagnostic contract,
/// because that is the whole reason the two copies this replaced were consolidated onto this
/// implementation rather than onto the shorter one: a mirror drift has to be readable from the
/// CI log, and a size mismatch has to be distinguishable from a content mismatch without
/// downloading either file.
/// </summary>
public sealed class TestFilesTests : IDisposable
{
    private readonly TempStoreRoot _temp = new("testfiles-selftest");

    public void Dispose() => _temp.Dispose();

    private string FileHolding(string name, byte[] content)
    {
        var path = Path.Combine(_temp.NewFolder("pair"), name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private string FileHolding(string name, string content) =>
        FileHolding(name, Encoding.UTF8.GetBytes(content));

    // The happy path: identical content at two different paths is what a healthy mirror looks
    // like, and it has to pass without the caller having to say anything about it.
    [Fact]
    public void Identical_content_at_two_paths_passes()
    {
        TestFiles.AssertSameBytes(
            FileHolding("expected.bin", "the same bytes"),
            FileHolding("actual.bin", "the same bytes"),
            "these must match");
    }

    // The case Assert.Equal(byte[], byte[]) would have answered with a dump of both arrays.
    // Equal sizes are the interesting half of a drift, so the message has to say that is what
    // happened AND repeat the caller's reason, or the reader is left with two hex blobs.
    [Fact]
    public void Same_size_but_different_content_reports_the_reason_and_the_difference()
    {
        const string why = "TarkovHelper/Assets and data/v1 must serve identical bytes";

        var failure = Assert.ThrowsAny<XunitException>(() => TestFiles.AssertSameBytes(
            FileHolding("expected.bin", "aaaa"),
            FileHolding("actual.bin", "bbbb"),
            why));

        Assert.Contains(why, failure.Message);
        Assert.Contains("same size but differ", failure.Message);
    }

    // A length mismatch is answered before anything is hashed, and both counts are named: the
    // difference between them is usually the whole diagnosis.
    [Fact]
    public void A_size_mismatch_names_both_byte_counts()
    {
        var failure = Assert.ThrowsAny<XunitException>(() => TestFiles.AssertSameBytes(
            FileHolding("expected.bin", "1234567890"),
            FileHolding("actual.bin", "1234"),
            "the mirror is stale"));

        Assert.Contains("the mirror is stale", failure.Message);
        Assert.Contains("is 10 bytes", failure.Message);
        Assert.Contains("is 4 bytes", failure.Message);
        Assert.DoesNotContain("same size but differ", failure.Message);
    }

    // The optional reason is what lets the publish side call this with two arguments. Omitting
    // it must still leave a usable message rather than a stray blank line or a null in it.
    [Fact]
    public void An_omitted_reason_still_names_both_paths()
    {
        var expected = FileHolding("expected.bin", "1234567890");
        var actual = FileHolding("actual.bin", "1234");

        var failure = Assert.ThrowsAny<XunitException>(
            () => TestFiles.AssertSameBytes(expected, actual));

        Assert.Contains(expected, failure.Message);
        Assert.Contains(actual, failure.Message);
        // Starts at the first path, so an omitted reason costs neither a leading blank line
        // nor the word null where a sentence belongs.
        Assert.StartsWith($"  {expected} is 10 bytes", failure.Message);
    }

    // A file that is not there is its own failure, not a comparison that happens to differ:
    // "the publish never wrote this" and "the publish wrote the wrong thing" are different bugs.
    [Fact]
    public void A_missing_file_is_named_rather_than_compared()
    {
        var present = FileHolding("expected.bin", "content");
        var absent = Path.Combine(_temp.NewFolder("gone"), "actual.bin");

        var missingActual = Assert.ThrowsAny<XunitException>(
            () => TestFiles.AssertSameBytes(present, absent, "why"));
        Assert.Contains($"{absent} is missing", missingActual.Message);

        var missingExpected = Assert.ThrowsAny<XunitException>(
            () => TestFiles.AssertSameBytes(absent, present, "why"));
        Assert.Contains($"{absent} is missing", missingExpected.Message);
    }

    // Two empty files are equal, and the digest step must not be what decides that: a zero byte
    // payload at both ends is a real state (a truncated publish) and it has to compare clean.
    [Fact]
    public void Two_empty_files_are_the_same_bytes()
    {
        TestFiles.AssertSameBytes(
            FileHolding("expected.bin", []),
            FileHolding("actual.bin", []));
    }
}
