using System.IO;
using System.Text;

namespace TarkovHelper.Tests;

/// <summary>
/// The guard on the shared digest spellings. The three copies this replaced had already drifted
/// on casing, so the facts that matter here are that the two overloads agree and that the
/// spelling is fixed rather than whatever the last copy happened to return.
/// </summary>
public sealed class TestDigestTests : IDisposable
{
    /// <summary>sha256("abc"), the published FIPS 180-2 example.</summary>
    private const string AbcDigest = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    private readonly TempStoreRoot _temp = new("testdigest-selftest");

    public void Dispose() => _temp.Dispose();

    private string FileHolding(byte[] content)
    {
        var path = Path.Combine(_temp.NewFolder("content"), "payload.bin");
        File.WriteAllBytes(path, content);
        return path;
    }

    // Against a known vector rather than against the helper's own other overload, so this cannot
    // pass by both sides being wrong the same way.
    [Fact]
    public void The_hex_of_known_content_is_the_published_digest()
    {
        Assert.Equal(AbcDigest, TestDigest.Sha256Hex(Encoding.UTF8.GetBytes("abc")));
        Assert.Equal(AbcDigest, TestDigest.Sha256Hex(FileHolding(Encoding.UTF8.GetBytes("abc"))));
    }

    // The drift the shared helper exists to end: one copy returned uppercase and lowered it at a
    // call site, so an expected value could look wrong for a reason unrelated to the bytes.
    [Fact]
    public void Both_overloads_return_lowercase_hex()
    {
        var content = Encoding.UTF8.GetBytes("Mixed Case Content");

        var fromBytes = TestDigest.Sha256Hex(content);
        var fromPath = TestDigest.Sha256Hex(FileHolding(content));

        Assert.Equal(fromBytes, fromPath);
        Assert.Equal(fromBytes.ToLowerInvariant(), fromBytes);
        Assert.DoesNotContain(fromBytes, char.IsUpper);
    }

    // The manifest field is the prefixed form, and the prefix is lowercase even though the hex
    // beside it is matched case-insensitively.
    [Fact]
    public void The_digest_is_the_prefixed_form_a_manifest_carries()
    {
        Assert.Equal($"sha256:{AbcDigest}", TestDigest.Sha256Digest(Encoding.UTF8.GetBytes("abc")));
        Assert.Equal($"sha256:{AbcDigest}", TestDigest.Sha256Digest(FileHolding(Encoding.UTF8.GetBytes("abc"))));
    }

    // Empty content still has a digest, and it is not the empty string: a publisher hashing a
    // zero-byte payload must produce something a reader can compare rather than nothing at all.
    [Fact]
    public void Empty_content_hashes_to_the_empty_digest_rather_than_to_nothing()
    {
        const string emptyDigest = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

        Assert.Equal(emptyDigest, TestDigest.Sha256Hex([]));
        Assert.Equal(emptyDigest, TestDigest.Sha256Hex(FileHolding([])));
    }
}
