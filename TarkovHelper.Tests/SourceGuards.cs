using System.IO;

namespace TarkovHelper.Tests;

/// <summary>
/// The two primitives behind the structural guards that read app source instead of running it:
/// a file under the repo root, and one member's body out of it. Shared by
/// <see cref="RefreshCoalescerSchedulingTests"/> and <see cref="CollectorPageSubscriptionTests"/>,
/// which used to be on their way to a third copy of the brace matcher.
/// <para>
/// Source guards exist for the WPF pages and the main window, none of which can be constructed
/// in this suite (their markup resolves App.xaml's resources and their constructors reach the
/// singletons that open the databases), and whose defects are a missing wiring on one path that
/// no reachable behavioural test can prove absent.
/// </para>
/// </summary>
internal static class SourceGuards
{
    /// <summary>The text of a source file under the repo root, failing loudly when it moved.</summary>
    internal static string Read(params string[] relativeParts)
    {
        var path = Path.Combine(TestRepo.Root(), Path.Combine(relativeParts));
        Assert.True(File.Exists(path), $"Source file not found: {path}");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// The body of the member whose declaration contains <paramref name="signature"/>, braces
    /// included. Naive brace matching is enough for these members: none of them contains a brace
    /// inside a string or comment that is not itself balanced.
    /// </summary>
    internal static string MemberBody(string source, string signature)
    {
        var declaration = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(declaration >= 0, $"'{signature}' no longer exists; update this test with it.");

        var open = source.IndexOf('{', declaration);
        Assert.True(open >= 0, $"'{signature}' has no body.");

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }

        throw new InvalidOperationException($"Unbalanced braces after '{signature}'.");
    }
}
