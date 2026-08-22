using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using TarkovDBEditor.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// A throwaway copy of everything a refresh reads: the wiki quest cache, the tarkov.dev task and
/// trader caches, and a database to start from. Lets the real
/// <see cref="RefreshDataService"/> run end to end without a network or the editor's own
/// working folder.
/// </summary>
internal sealed class RefreshPipelineFixture : IDisposable
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public RefreshPipelineFixture()
    {
        BasePath = Path.Combine(Path.GetTempPath(), "refresh-fixture-" + Guid.NewGuid().ToString("N"));
        CacheDir = Path.Combine(BasePath, "wiki_data", "cache");
        Directory.CreateDirectory(CacheDir);

        DatabasePath = Path.Combine(BasePath, "tarkov_data.db");
    }

    public string BasePath { get; }
    public string CacheDir { get; }
    public string DatabasePath { get; }

    public string QuestCachePath => Path.Combine(CacheDir, "quest_cache.json");
    public string TaskCachePath => Path.Combine(CacheDir, "tarkov_dev_quests.json");
    public string TraderCachePath => Path.Combine(CacheDir, "tarkov_dev_traders.json");

    public RefreshDataService CreateService() => new(BasePath);

    public Task<RefreshResult> RefreshAsync() =>
        CreateService().RefreshDataFromCacheAsync(DatabasePath);

    #region Caches

    /// <summary>Writes the wiki crawl cache, one entry per page.</summary>
    public RefreshPipelineFixture WithWikiPages(params (string Title, string Content)[] pages)
    {
        var cache = new QuestCacheFile { LastUpdated = DateTime.UtcNow };
        foreach (var (title, content) in pages)
        {
            cache.Quests[title] = new CachedQuestInfo
            {
                QuestName = title,
                PageContent = content,
                RevisionId = 1,
                CachedAt = DateTime.UtcNow,
                ContentFetchedAt = DateTime.UtcNow,
                IsSeasonal = WikiQuestService.ExtractIsSeasonal(content),
            };
        }

        File.WriteAllText(QuestCachePath, JsonSerializer.Serialize(cache, WriteOptions));
        return this;
    }

    /// <summary>Writes an empty wiki cache: the shape a failed crawl leaves behind.</summary>
    public RefreshPipelineFixture WithNoWikiPages()
    {
        File.WriteAllText(QuestCachePath, JsonSerializer.Serialize(new QuestCacheFile
        {
            LastUpdated = DateTime.UtcNow,
        }, WriteOptions));
        return this;
    }

    public RefreshPipelineFixture WithTasks(params TarkovDevQuestCacheItem[] tasks)
    {
        File.WriteAllText(TaskCachePath, JsonSerializer.Serialize(new TarkovDevQuestsCache
        {
            CachedAt = DateTime.UtcNow,
            Quests = tasks.ToList(),
        }, WriteOptions));
        return this;
    }

    public RefreshPipelineFixture WithNoTaskCache()
    {
        if (File.Exists(TaskCachePath))
            File.Delete(TaskCachePath);
        return this;
    }

    public RefreshPipelineFixture WithTraders(params (string Id, string Name)[] traders)
    {
        File.WriteAllText(TraderCachePath, JsonSerializer.Serialize(new TarkovDevTradersCache
        {
            CachedAt = DateTime.UtcNow,
            Traders = traders.Select(t => new TarkovDevTraderCacheItem { Id = t.Id, Name = t.Name }).ToList(),
        }, WriteOptions));
        return this;
    }

    public RefreshPipelineFixture WithNoTraderCache()
    {
        if (File.Exists(TraderCachePath))
            File.Delete(TraderCachePath);
        return this;
    }

    /// <summary>
    /// Backdates the task cache file. The staleness guard reads the file's write time, which is
    /// when the data was last confirmed current rather than when it was first downloaded.
    /// </summary>
    public RefreshPipelineFixture WithTaskCacheLastConfirmed(DateTime when)
    {
        File.SetLastWriteTime(TaskCachePath, when);
        return this;
    }

    #endregion

    #region Database

    /// <summary>
    /// Creates the database a refresh starts from, shaped like a published one: no
    /// <c>NormalizedName</c> column (it arrives with this refresh) and no
    /// <c>QuestTraderRequirements</c> table.
    /// </summary>
    public RefreshPipelineFixture WithDatabase(params (string Name, string? BsgId)[] quests)
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();

        Execute(connection, """
            CREATE TABLE Items (
                Id TEXT PRIMARY KEY,
                BsgId TEXT,
                Name TEXT NOT NULL,
                NameEN TEXT, NameKO TEXT, NameJA TEXT,
                ShortNameEN TEXT, ShortNameKO TEXT, ShortNameJA TEXT,
                WikiPageLink TEXT, IconUrl TEXT, Category TEXT, Categories TEXT,
                UpdatedAt TEXT)
            """);

        Execute(connection, """
            CREATE TABLE Quests (
                Id TEXT PRIMARY KEY,
                BsgId TEXT,
                Name TEXT NOT NULL,
                NameEN TEXT, NameKO TEXT, NameJA TEXT,
                WikiPageLink TEXT,
                Trader TEXT,
                Location TEXT,
                MinLevel INTEGER,
                MinLevelApproved INTEGER NOT NULL DEFAULT 0,
                MinLevelApprovedAt TEXT,
                MinScavKarma INTEGER,
                MinScavKarmaApproved INTEGER NOT NULL DEFAULT 0,
                MinScavKarmaApprovedAt TEXT,
                KappaRequired INTEGER NOT NULL DEFAULT 0,
                Faction TEXT,
                RequiredEdition TEXT,
                RequiredEditionApproved INTEGER NOT NULL DEFAULT 0,
                RequiredEditionApprovedAt TEXT,
                ExcludedEdition TEXT,
                ExcludedEditionApproved INTEGER NOT NULL DEFAULT 0,
                ExcludedEditionApprovedAt TEXT,
                RequiredDecodeCount INTEGER,
                RequiredDecodeCountApproved INTEGER NOT NULL DEFAULT 0,
                RequiredDecodeCountApprovedAt TEXT,
                RequiredPrestigeLevel INTEGER,
                RequiredPrestigeLevelApproved INTEGER NOT NULL DEFAULT 0,
                RequiredPrestigeLevelApprovedAt TEXT,
                IsApproved INTEGER NOT NULL DEFAULT 0,
                ApprovedAt TEXT,
                UpdatedAt TEXT)
            """);

        // Present in every published database, and the refresh only creates it when it has rows
        // to write, so the fixture has to carry it for a "no rows were written" assertion to
        // mean anything.
        Execute(connection, """
            CREATE TABLE QuestRequirements (
                Id TEXT PRIMARY KEY,
                QuestId TEXT NOT NULL,
                RequiredQuestId TEXT NOT NULL,
                RequirementType TEXT NOT NULL DEFAULT 'Complete',
                DelayMinutes INTEGER,
                GroupId INTEGER NOT NULL DEFAULT 0,
                ContentHash TEXT,
                IsApproved INTEGER NOT NULL DEFAULT 0,
                ApprovedAt TEXT,
                UpdatedAt TEXT,
                FOREIGN KEY (QuestId) REFERENCES Quests(Id) ON DELETE CASCADE,
                FOREIGN KEY (RequiredQuestId) REFERENCES Quests(Id) ON DELETE CASCADE)
            """);

        foreach (var (name, bsgId) in quests)
        {
            using var cmd = new SqliteCommand(
                "INSERT INTO Quests (Id, BsgId, Name, WikiPageLink, KappaRequired) VALUES (@Id, @BsgId, @Name, @Link, 0)",
                connection);
            cmd.Parameters.AddWithValue("@Id", WikiQuestIdentity.IdFor(name));
            cmd.Parameters.AddWithValue("@BsgId", (object?)bsgId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Name", name);
            cmd.Parameters.AddWithValue("@Link", WikiQuestIdentity.PageLinkFor(name));
            cmd.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        return this;
    }

    /// <summary>Reads back one column for every quest row, keyed by name.</summary>
    public Dictionary<string, string?> ReadQuestColumn(string column)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);

        using (var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly"))
        {
            connection.Open();
            using var cmd = new SqliteCommand($"SELECT Name, {column} FROM Quests", connection);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                values[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetValue(1).ToString();
        }

        SqliteConnection.ClearAllPools();
        return values;
    }

    /// <summary>Runs a query that returns rows of strings, for the child tables.</summary>
    public List<string[]> Query(string sql)
    {
        var rows = new List<string[]>();

        using (var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly"))
        {
            connection.Open();
            using var cmd = new SqliteCommand(sql, connection);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var row = new string[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                    row[i] = reader.IsDBNull(i) ? "" : reader.GetValue(i).ToString() ?? "";
                rows.Add(row);
            }
        }

        SqliteConnection.ClearAllPools();
        return rows;
    }

    public bool TableExists(string tableName) =>
        Query($"SELECT name FROM sqlite_master WHERE type='table' AND name='{tableName}'").Count > 0;

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = new SqliteCommand(sql, connection);
        cmd.ExecuteNonQuery();
    }

    #endregion

    #region Page and task builders

    /// <summary>Wiki page text with the fields the parsers read.</summary>
    public static string Page(string trader = "Prapor", string location = "Customs", int? minLevel = null, string? extraRequirement = null)
    {
        var requirements = new List<string>();
        if (minLevel.HasValue)
            requirements.Add($"* Must be level {minLevel} to start this quest.");
        if (extraRequirement != null)
            requirements.Add(extraRequirement);

        // Built by concatenation rather than a raw interpolated string: wiki markup is all
        // braces, which fight every interpolation delimiter C# has.
        var text = new System.Text.StringBuilder();
        text.AppendLine("{{Infobox quest");
        text.AppendLine("|given by = [[" + trader + "]]");
        text.AppendLine("|location = [[" + location + "]]");
        text.AppendLine("}}");
        text.AppendLine("==Requirements==");
        foreach (var requirement in requirements)
            text.AppendLine(requirement);
        text.AppendLine("==Objectives==");
        text.AppendLine("* Eliminate 10 Scavs on [[" + location + "]]");
        return text.ToString();
    }

    /// <summary>A page the wiki marks as playable only in the current season.</summary>
    public static string SeasonalPage() =>
        Page(extraRequirement: "* Must be playing in the [[Seasons#Season 1: KORD BREACH|Seasonal mode]].");

    public static TarkovDevQuestCacheItem Task(
        string id,
        string title,
        string traderId = "54cb50c76803fa8b248b4571",
        int minPlayerLevel = 0,
        bool kappaRequired = false,
        string faction = "Any",
        (string TraderId, int Level)[]? loyalty = null,
        (string TaskId, string Status)[]? requires = null) =>
        new()
        {
            Id = id,
            NameEN = title,
            NormalizedName = QuestIdentityResolver.NormalizeQuestName(title),
            WikiLink = WikiQuestIdentity.PageLinkFor(title),
            Trader = traderId,
            MinPlayerLevel = minPlayerLevel,
            KappaRequired = kappaRequired,
            FactionName = faction,
            TraderLevelRequirements = (loyalty ?? Array.Empty<(string, int)>())
                .Select(l => new TarkovDevTaskTraderLevel { TraderId = l.TraderId, Level = l.Level })
                .ToList(),
            TaskRequirements = (requires ?? Array.Empty<(string, string)>())
                .Select(r => new TarkovDevTaskPrerequisite { TaskId = r.TaskId, Status = new List<string> { r.Status } })
                .ToList(),
        };

    #endregion

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(BasePath))
                Directory.Delete(BasePath, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }
}
