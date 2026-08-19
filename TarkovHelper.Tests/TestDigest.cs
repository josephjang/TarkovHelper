using System.IO;
using System.Security.Cryptography;

namespace TarkovHelper.Tests;

/// <summary>
/// The sha256 spellings the data channel suites assert against. Three suites had each grown
/// their own copy, and they had already drifted: two returned lowercase hex and the third
/// returned uppercase and lowered it at one of its call sites, which is exactly the kind of
/// difference that makes an expected value look wrong for a reason that has nothing to do
/// with the bytes. One spelling, in one place.
/// </summary>
internal static class TestDigest
{
    /// <summary>
    /// Lowercase hex, with no algorithm prefix. Lowercase because that is what the publisher
    /// writes; the reader matches case-insensitively, and a suite that wants to prove that
    /// uppercases this result itself.
    /// </summary>
    internal static string Sha256Hex(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    /// <summary>Same digest, streamed, for content that is already a file.</summary>
    internal static string Sha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// The full <c>sha256:&lt;hex&gt;</c> string a channel manifest carries. The algorithm
    /// prefix is part of the published value rather than decoration, so the helper owns it:
    /// every call site that pasted it on by hand was building the same string.
    /// </summary>
    internal static string Sha256Digest(byte[] content) => "sha256:" + Sha256Hex(content);

    /// <inheritdoc cref="Sha256Digest(byte[])"/>
    internal static string Sha256Digest(string path) => "sha256:" + Sha256Hex(path);
}
