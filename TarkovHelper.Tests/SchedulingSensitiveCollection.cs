using System.IO;

namespace TarkovHelper.Tests;

/// <summary>
/// The one collection for every test class that blocks threads, spins, sleeps, gates tasks or
/// measures time. Its <c>DisableParallelization</c> makes xunit run these classes one at a time
/// with nothing else running beside them, so each one gets a quiet machine.
/// <para>
/// Why they cannot share the machine: xunit runs one collection (by default, one class) per
/// worker, ProcessorCount workers at once, and those workers ARE thread-pool threads. A GitHub
/// runner has 4 cores, so four classes that sleep inside a publish gate, join dispatcher
/// threads, or hot-spin a reader can occupy every worker at once; timer callbacks and
/// <c>Task.Run</c> bodies then queue behind them for seconds. On that machine a 200 ms
/// <c>WaitAsync</c> was observed resuming after 13-22 s, a writer task was cancelled before its
/// first iteration ran, and 5-second poll deadlines expired, each reported as a different flaky
/// test on a different run (PR #46). A 16-core dev machine has workers to spare, which is why
/// none of it ever reproduced locally.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SchedulingSensitiveCollection
{
    public const string Name = "scheduling-sensitive";
}

/// <summary>
/// Keeps the collection honest: membership is by what a test file DOES, not by whether its
/// author remembered the attribute. A new test class that sleeps or spins outside the collection
/// reintroduces the cross-class starvation the collection exists to end, and nothing else would
/// say so until it flakes on a loaded runner.
/// </summary>
public sealed class SchedulingSensitiveCollectionTests
{
    /// <summary>
    /// Source markers that make a test class scheduling-sensitive: each one either occupies a
    /// worker without yielding it (sleeps, joins, spins, sync-over-async), schedules work whose
    /// start time it then assumes (<c>Task.Run</c>, <c>Task.Delay</c>), or asserts on the clock.
    /// </summary>
    private static readonly string[] Markers =
    {
        "Thread.Sleep(",
        "new Thread(",
        "Task.Run(",
        "Task.Delay(",
        "TaskCompletionSource",
        "new CancellationTokenSource(",
        "Stopwatch",
        "Monitor.Enter(",
        "Parallel.",
        // Argument-typed on purpose: source-scan tests assert production snippets like
        // "_questEventGate.WaitAsync()" as string literals, which are not waits of their own.
        ".WaitAsync(TimeSpan",
        ".WaitAsync(HangGuard",
        "GetAwaiter().GetResult()",
        "SpinWait",
        "SpinUntil",
    };

    [Fact]
    public void Every_test_class_that_blocks_spins_or_measures_time_is_in_the_collection()
    {
        var testsDir = Path.Combine(TestRepo.Root(), "TarkovHelper.Tests");
        var members = new List<string>();
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(testsDir, "*.cs"))
        {
            var name = Path.GetFileName(file);

            // This file holds the marker list itself, as string literals.
            if (name == "SchedulingSensitiveCollection.cs") continue;

            var source = File.ReadAllText(file);

            // Helpers declare no tests, so they schedule nothing on their own; whichever test
            // class calls them carries the marker at its own call site or not at all.
            if (!source.Contains("[Fact]") && !source.Contains("[Theory]")) continue;

            // The e2e suite runs in its own serial collection already, and only on an
            // interactive desktop (CI filters Category!=E2E out).
            if (source.Contains("[Trait(\"Category\", \"E2E\")]")) continue;

            var marker = Array.Find(Markers, m => source.Contains(m, StringComparison.Ordinal));
            if (marker == null) continue;

            if (source.Contains("[Collection(SchedulingSensitiveCollection.Name)]", StringComparison.Ordinal))
            {
                members.Add(name);
            }
            else
            {
                violations.Add($"{name} (uses {marker})");
            }
        }

        // Proves the scan still finds anything at all: a moved attribute spelling or a renamed
        // marker would otherwise turn this into a check over an empty list that passes for the
        // wrong reason.
        Assert.Contains("ProgressStoreFakeTests.cs", members);
        Assert.Contains("RefreshCoalescerSchedulingTests.cs", members);

        Assert.True(violations.Count == 0,
            "These test classes block, spin, sleep or measure time, but do not declare " +
            "[Collection(SchedulingSensitiveCollection.Name)]. Running them in parallel with " +
            "other classes starves the four workers of a CI runner and turns their timing " +
            "assumptions into flakes; add the attribute (or drop the blocking construct):\n" +
            string.Join("\n", violations));
    }
}
