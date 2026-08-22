namespace DataDiff;

/// <summary>What the published icon folder holds against what the item table needs.</summary>
public sealed class IconCoverageResult
{
    /// <summary>
    /// False when the folder does not exist, in which case nothing was measured and the counts
    /// below are all empty. Required rather than defaulted so a new construction site cannot
    /// forget to say which of the two it is: "no icons found" and "nowhere to look" read
    /// identically in the numbers and mean opposite things.
    /// </summary>
    public required bool DirectoryExists { get; init; }

    public int ItemsWithIcon { get; init; }
    public required List<string> ItemsWithoutIcon { get; init; }
    public required List<string> OrphanFiles { get; init; }
    public required List<string> NonPngFiles { get; init; }
}

/// <summary>
/// Matches item row keys against icon files.
/// <para>
/// Icons ship inside app releases as <c>Assets/icons/{Items.Id}.png</c>, and the app reads
/// exactly that name, so an item whose row key changed, or whose icon was downloaded as
/// something other than a PNG, silently shows no picture. Both are invisible in the database
/// itself, which is why the report checks the folder.
/// </para>
/// </summary>
public static class IconCoverage
{
    public static IconCoverageResult Measure(IReadOnlyList<ItemRow> items, string iconDirectory)
    {
        // Nothing is measured against a folder that is not there. Reporting every item as
        // uncovered would be a measurement, and a mistyped path would then read as the release
        // having lost all of its icons.
        if (!Directory.Exists(iconDirectory))
        {
            return new IconCoverageResult
            {
                DirectoryExists = false,
                ItemsWithIcon = 0,
                ItemsWithoutIcon = new List<string>(),
                OrphanFiles = new List<string>(),
                NonPngFiles = new List<string>(),
            };
        }

        // File names are compared case-insensitively because Windows resolves them that way and
        // the repository has historically tracked the folder under two case spellings.
        var pngStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nonPng = new List<string>();

        foreach (var file in Directory.EnumerateFiles(iconDirectory))
        {
            var extension = Path.GetExtension(file);
            if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
                pngStems.Add(Path.GetFileNameWithoutExtension(file));
            else
                nonPng.Add(Path.GetFileName(file));
        }

        var withIcon = 0;
        var withoutIcon = new List<string>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            if (pngStems.Contains(item.Id))
            {
                withIcon++;
                claimed.Add(item.Id);
            }
            else
            {
                withoutIcon.Add(item.Name);
            }
        }

        return new IconCoverageResult
        {
            DirectoryExists = true,
            ItemsWithIcon = withIcon,
            ItemsWithoutIcon = withoutIcon,
            OrphanFiles = pngStems.Where(stem => !claimed.Contains(stem)).Select(stem => stem + ".png").ToList(),
            NonPngFiles = nonPng,
        };
    }
}
