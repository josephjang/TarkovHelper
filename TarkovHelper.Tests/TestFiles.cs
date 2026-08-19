using System.IO;

namespace TarkovHelper.Tests;

/// <summary>
/// The one spelling of "these two files hold the same bytes", for the suites that assert the
/// data channel's mirror invariant. Two suites had each grown their own copy and they had
/// already drifted on the thing that matters most about this assertion: what it prints when it
/// fails. One compared lengths and then digests and named the reason the caller cared; the
/// other handed two <c>byte[]</c> to <c>Assert.Equal</c>, which on the 6.9 MB channel database
/// turns a one line mirror drift into an unreadable dump. Whichever copy the next test author
/// reached for decided whether a red CI check could be diagnosed.
/// </summary>
internal static class TestFiles
{
    /// <summary>
    /// Asserts that both paths exist and hold identical bytes, size first so a plain
    /// truncation says so in one line, then sha256 so equal sizes still cannot pass by luck.
    /// </summary>
    /// <param name="why">
    /// What the caller loses when these files disagree, printed above the paths. Optional
    /// because the publish side asserts inside a test whose name already says it, while the
    /// repository side is read by whoever broke CI without opening the suite.
    /// </param>
    internal static void AssertSameBytes(string expectedPath, string actualPath, string? why = null)
    {
        Assert.True(File.Exists(expectedPath), $"{expectedPath} is missing");
        Assert.True(File.Exists(actualPath), $"{actualPath} is missing");

        var prefix = why == null ? "" : why + "\n";

        var expectedLength = new FileInfo(expectedPath).Length;
        var actualLength = new FileInfo(actualPath).Length;
        Assert.True(expectedLength == actualLength,
            $"{prefix}  {expectedPath} is {expectedLength} bytes\n  {actualPath} is {actualLength} bytes");

        // Streamed through TestDigest rather than ReadAllBytes: the mirror asserts against the
        // committed multi-megabyte database, and the failure message has to stay readable.
        Assert.True(TestDigest.Sha256Hex(expectedPath) == TestDigest.Sha256Hex(actualPath),
            $"{prefix}  {expectedPath} and {actualPath} are the same size but differ.");
    }
}
