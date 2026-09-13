using System.IO;

namespace TarkovHelper.Tests;

/// <summary>
/// Which files under <c>TarkovHelper/Pages</c> are allowed to read a quest's status at all.
/// <para>
/// A page that renders from a <see cref="TarkovHelper.Pages.RenderPass"/> and then reads one
/// status live shows a row from one profile beside a badge from another, which is the tearing
/// the pass exists to make unobservable - and the page that did it would look correct, because
/// every line of it reads plausibly on its own. Structure is what rules it out: the pass answers
/// the status itself, so the remaining question is only whether anything under Pages/ still asks
/// the service, and that is a question a source scan can settle for the whole directory instead
/// of for the files a reviewer happened to open.
/// </para>
/// </summary>
public sealed class RenderPassStatusGuardTests
{
    /// <summary>The file whose job is to read one: the pass itself.</summary>
    private const string Reader = "RenderPass.cs";

    /// <summary>
    /// The pages that still ask the service per quest, named here on purpose so this list can
    /// only shrink. The map page paints its markers off the live singletons and captures no pass;
    /// converting it means deleting a line here. A NEW page reading a live status fails this case
    /// instead of passing review.
    /// </summary>
    private static readonly string[] StillReadLive = { Path.Combine("Map", "MapPage.xaml.cs") };

    [Fact]
    public void RenderPass_is_the_only_status_reader_under_Pages()
    {
        var scanned = 0;

        foreach (var (name, text) in SourceGuards.ReadTree("TarkovHelper", "Pages"))
        {
            // Comment lines are stripped first: several files name GetStatus in a see cref or in
            // prose about what the code used to do, and neither is a reading.
            var code = string.Join('\n', text.Split('\n')
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            var reads = code.Contains(".GetStatus(", StringComparison.Ordinal);
            scanned++;

            if (name == Reader || StillReadLive.Contains(name))
            {
                Assert.True(reads, $"{name} no longer reads a status; drop it from this list.");
            }
            else
            {
                Assert.False(reads, $"{name} reads a status itself; capture a RenderPass and ask it.");
            }
        }

        // The scan walked the tree rather than an empty directory, so a green run means something.
        Assert.InRange(scanned, StillReadLive.Length + 2, int.MaxValue);
    }
}
