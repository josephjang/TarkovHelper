using DataDiff;
using Microsoft.Data.Sqlite;

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

// A mistyped --icons reads, in the report, exactly like every icon in the release going missing.
// Refuse the run instead of printing that; the report is only worth reading if its numbers mean
// what they say.
if (iconDirectory != null && !Directory.Exists(iconDirectory))
    return Fail($"Icon folder not found: {iconDirectory}");

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
// SqliteException is in the list because a file that is not a database, or one this build cannot
// open, must leave a one line message rather than a stack trace and an empty report.
catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or SqliteException)
{
    return Fail(ex.Message);
}

static int Fail(string message)
{
    Console.Error.WriteLine($"DataDiff: {message}");
    Console.Error.Write(Usage);
    return 1;
}
