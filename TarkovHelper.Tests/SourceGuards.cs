using System.IO;
using System.Runtime.ExceptionServices;

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
    /// <summary>
    /// Every .cs file under a directory below the repo root, as (path relative to that directory,
    /// text). What a guard over a WHOLE directory is written against, so the rule it pins holds
    /// for the files nobody thought of as well as the ones it names.
    /// </summary>
    internal static IEnumerable<(string Name, string Text)> ReadTree(params string[] relativeParts)
    {
        var root = Path.Combine(TestRepo.Root(), Path.Combine(relativeParts));
        Assert.True(Directory.Exists(root), $"Source directory not found: {root}");
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Select(path => (Path.GetRelativePath(root, path), File.ReadAllText(path)));
    }

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

/// <summary>
/// Runs a body on an STA thread, as constructing a WPF element or window requires, and rethrows
/// whatever it threw on the caller's thread with its stack intact. One helper rather than a copy
/// per suite: the drawer layout cases, the Kappa window's placement case and the requirement
/// palette all need the same thread.
/// <para>
/// A class that calls this JOINS a thread, so it must declare
/// <c>[Collection(SchedulingSensitiveCollection.Name)]</c>. The marker scan in
/// SchedulingSensitiveCollection.cs recognises such a caller by the <c>StaThread.Run(</c>
/// marker, so the calling class is classified as scheduling-sensitive: when the attribute
/// is missing, the scan reports that class as a violation and its guard fails, rather than
/// waving the call through unseen.
/// </para>
/// </summary>
internal static class StaThread
{
    internal static T Run<T>(Func<T> body)
    {
        var result = default(T);
        ExceptionDispatchInfo? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = body();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                // Element construction spins up a dispatcher for this thread; without this it
                // outlives the thread and every case leaks one.
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        })
        {
            IsBackground = true,
            Name = nameof(StaThread),
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "the STA thread never finished");
        failure?.Throw();
        return result!;
    }
}
