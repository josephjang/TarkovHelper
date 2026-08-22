using DataDiff;

// A thin shell over DiffReport: parse arguments, read two databases, print markdown. Everything
// worth testing lives in the library classes beside this file.
//
//   dotnet run --project tools/DataDiff -- <previous.db> <candidate.db> [--icons <dir>] [--log <refresh.json>] > report.md

const string Usage =
    "Usage: DataDiff <previous.db> <candidate.db> [--icons <dir>] [--log <refresh.json>]\n"
    + "\n"
    + "Writes a markdown comparison of two published databases to stdout: quests added, removed\n"
    + "and renamed, every field change, prerequisite edges, loyalty gates, objective lists whose\n"
    + "shape changed, items, icon coverage, hideout joins and NULL rates.\n"
    + "\n"
    + "  --icons <dir>        Item icon folder to check coverage against (Assets/icons).\n"
    + "  --log <refresh.json> The refresh log the regeneration wrote, for what never reached the\n"
    + "                       database: pages held back, records with no page, source disagreements.\n";

var positional = new List<string>();
string? iconDirectory = null;
string? logPath = null;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--icons":
            if (++i >= args.Length) return Fail("--icons needs a directory.");
            iconDirectory = args[i];
            break;
        case "--log":
            if (++i >= args.Length) return Fail("--log needs a file.");
            logPath = args[i];
            break;
        case "-h":
        case "--help":
            Console.Out.Write(Usage);
            return 0;
        default:
            positional.Add(args[i]);
            break;
    }
}

if (positional.Count != 2)
    return Fail("Expected exactly two database paths.");

try
{
    var previous = DataSnapshot.Read(positional[0]);
    var candidate = DataSnapshot.Read(positional[1]);
    var options = new DiffOptions
    {
        IconDirectory = iconDirectory,
        RefreshLog = logPath == null ? null : RefreshLog.Read(logPath),
    };

    Console.Out.Write(DiffReport.Render(previous, candidate, options));
    return 0;
}
catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
{
    return Fail(ex.Message);
}

static int Fail(string message)
{
    Console.Error.WriteLine($"DataDiff: {message}");
    Console.Error.Write(Usage);
    return 1;
}
