using TarkovHelper.Models;

namespace TarkovHelper.Tests;

/// <summary>
/// Quest fixtures shared by the progress, cascade and log-sync tests. One home for the shape,
/// because the shape is load-bearing: a task's Ids entry is the key its progress row is WRITTEN
/// under and its NormalizedName is the key that row is READ back under, so a fixture that
/// carried only one of the two would quietly stop exercising the dual-key paths.
/// </summary>
internal static class TestTasks
{
    /// <summary>
    /// A quest with one Id and a name used as both display name and NormalizedName. No
    /// requirements and no alternatives, so a completion writes exactly one row.
    /// </summary>
    internal static TarkovTask Quest(string id, string name) => new()
    {
        Ids = new List<string> { id },
        Name = name,
        NormalizedName = name,
        Trader = "Prapor",
    };
}
