using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TarkovDBEditor.Services;

/// <summary>
/// Service for publishing DB updates from TarkovDBEditor to the repository.
/// Compares files using MD5 hash and copies changed files.
///
/// The database and its version stamp go to the data channel (data/v&lt;N&gt;/), where N is
/// the highest format directory present in the repo, and are mirrored into
/// TarkovHelper/Assets while that format is 1 (the pre-channel endpoint fielded builds
/// poll; the two must stay byte-identical). Everything else (map configs, SVGs, icons)
/// ships inside app releases and keeps publishing to Assets only.
///
/// This tool never creates a format directory: bumping the format is a deliberate act in
/// the same reviewed PR that teaches the app to read it, so a routine publish cannot bump
/// it by accident. Design: docs/decisions/feature-versioned-data-channel.spec.md.
///
/// Comparing is read-only and publishing is the only step that writes anything, including
/// the published database's own data format stamp.
/// </summary>
public class DataPublishService : IDisposable
{
    /// <summary>Repo-relative root of the data channel, holding one v&lt;N&gt; directory per format.</summary>
    private const string DataChannelDirName = "data";
    private const string DatabaseFileName = "tarkov_data.db";
    private const string VersionFileName = "db_version.txt";
    private const string ManifestFileName = "manifest.json";
    private const string IndexFileName = "index.json";

    /// <summary>
    /// Shape of the JSON documents this tool writes, in the sense Docker's manifest
    /// schemaVersion uses. Distinct from the data format, which is the contract of the
    /// database those documents describe. See feature-versioned-data-channel.spec.md.
    /// </summary>
    private const int ManifestSchemaVersion = 1;
    private const int IndexSchemaVersion = 1;

    /// <summary>The only format that is also served from the pre-channel Assets endpoint.</summary>
    private const int MirroredDataFormatVersion = 1;

    private readonly string _sourceBasePath;
    private readonly string _repoRootPath;
    private readonly string _targetBasePath;
    private readonly string _dataChannelPath;

    public DataPublishService()
        : this(
            AppDomain.CurrentDomain.BaseDirectory, // TarkovDBEditor Release build output path
            // Repo root, relative to that build output.
            Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..")))
    {
    }

    /// <summary>
    /// Explicit-path overload, used by tests to drive a publish against a throwaway tree
    /// instead of the real repository. The two publish targets are always derived from
    /// the repo root, so a test cannot accidentally point them at different trees.
    /// </summary>
    public DataPublishService(string sourceBasePath, string repoRootPath)
    {
        _sourceBasePath = sourceBasePath;
        _repoRootPath = repoRootPath;
        _targetBasePath = Path.Combine(_repoRootPath, "TarkovHelper", "Assets");
        _dataChannelPath = Path.Combine(_repoRootPath, DataChannelDirName);
    }

    public string SourceBasePath => _sourceBasePath;
    public string TargetBasePath => _targetBasePath;
    public string DataChannelPath => _dataChannelPath;

    /// <summary>
    /// The format this publish writes: the highest data/v&lt;N&gt; directory in the repo.
    /// Returns 0 when the channel is missing entirely, which callers must treat as an
    /// error rather than falling back to the Assets-only layout.
    /// </summary>
    public int GetLiveDataFormatVersion()
    {
        if (!Directory.Exists(_dataChannelPath)) return 0;

        var highest = 0;
        foreach (var dir in Directory.GetDirectories(_dataChannelPath, "v*"))
        {
            var name = Path.GetFileName(dir);
            if (int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var format)
                && format > highest)
            {
                highest = format;
            }
        }

        return highest;
    }

    /// <summary>Endpoint directory for a format, e.g. &lt;repo&gt;/data/v1.</summary>
    private string ChannelDirFor(int format) =>
        Path.Combine(_dataChannelPath, $"v{format.ToString(CultureInfo.InvariantCulture)}");

    /// <summary>
    /// Result of a comparison operation
    /// </summary>
    public class ComparisonResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }

        // Database (target = this repo's live data-channel endpoint)
        public bool DbExists { get; set; }
        public bool DbChanged { get; set; }
        public string? SourceDbHash { get; set; }
        public string? TargetDbHash { get; set; }
        public long SourceDbSize { get; set; }
        public long TargetDbSize { get; set; }

        /// <summary>
        /// The data format the source database currently declares in its own header, or
        /// null when there is no source database to ask. A publish rewrites this to the
        /// live format, so a value that is not already the live one means the bytes on
        /// the endpoint will differ from the bytes sitting in the build output now.
        /// </summary>
        public int? SourceDataFormatStamp { get; set; }

        // Data channel
        /// <summary>Format this publish writes, i.e. the highest data/v&lt;N&gt; in the repo.</summary>
        public int LiveDataFormatVersion { get; set; }
        public string? ChannelDirPath { get; set; }

        /// <summary>
        /// True while the live format is the one the pre-channel Assets endpoint also
        /// serves, so a publish must write both copies.
        /// </summary>
        public bool MirrorsToAssets { get; set; }

        /// <summary>State of the Assets mirror, written once by <c>CheckMirrorAsync</c>.</summary>
        public MirrorSyncState Mirror { get; set; } = MirrorSyncState.NotApplicable;

        // Version
        /// <summary>
        /// The version token the live channel endpoint publishes right now, or null when
        /// it has never published one. Null rather than a "0.0.0" placeholder, because a
        /// publish that is not replacing the database keeps this token instead of
        /// bumping it, and a placeholder would be indistinguishable from a real value.
        /// </summary>
        public string? CurrentVersion { get; set; }

        /// <summary>
        /// The version token the channel manifest carries, or null when the manifest is
        /// absent, unreadable or records none. Kept beside <see cref="CurrentVersion"/>
        /// because a half-applied publish can leave only one of the two documents behind,
        /// and the manifest is the one clients actually compare against.
        /// </summary>
        public string? ManifestVersion { get; set; }

        /// <summary>
        /// The token this endpoint publishes right now, from whichever document still
        /// holds it: <c>db_version.txt</c> first, then the manifest. Null when neither
        /// names one, i.e. when nothing has ever been published here, and equally when
        /// what they name is a token no client can read
        /// (<see cref="IsPublishableVersionToken"/>): a token carried forward is a token
        /// the next publish writes again, so keeping an unreadable one would republish
        /// the same unreadable channel forever with no way out of the tool.
        /// </summary>
        public string? PublishedVersion =>
            IsPublishableVersionToken(CurrentVersion) ? CurrentVersion
            : IsPublishableVersionToken(ManifestVersion) ? ManifestVersion
            : null;

        /// <summary>
        /// The token to publish next, suggested by incrementing
        /// <see cref="PublishedVersion"/>. Computed rather than stored, so it cannot
        /// contradict the token it is derived from.
        /// </summary>
        public string NewVersion =>
            Version.TryParse(PublishedVersion, out var published)
                ? $"{published.Major}.{published.Minor}.{published.Build + 1}"
                : "1.0.0";

        /// <summary>
        /// Why the channel manifest no longer describes the database beside it, or null
        /// when it does. The manifest is what clients trust to decide whether to download
        /// and whether to keep what they downloaded, so a drifted one has to be
        /// publishable: CI guards it, and a red guard the tool cannot clear leaves no way
        /// out of the editor.
        /// </summary>
        public string? ManifestDriftReason { get; set; }

        /// <summary>
        /// Why <c>data/index.json</c> no longer names this channel, or null when it does.
        /// The index is the one channel document that lives above the endpoints, and it
        /// is what tells a build pinned to an older format that nothing further is coming
        /// for it; CI guards it exactly as it guards the manifest, so a drifted index has
        /// to be publishable or it is a red build the tool cannot clear.
        /// </summary>
        public string? IndexDriftReason { get; set; }

        // Map configs
        public bool MapConfigsChanged { get; set; }
        public string? SourceMapConfigsHash { get; set; }
        public string? TargetMapConfigsHash { get; set; }

        // The asset groups, one value each: the files to copy and the counts behind them
        // travel together, so no caller can pair one group's list with another's count.
        public FileGroupComparison MapSvg { get; set; } = FileGroupComparison.Empty;
        public FileGroupComparison MarkerIcon { get; set; } = FileGroupComparison.Empty;
        public FileGroupComparison ItemIcon { get; set; } = FileGroupComparison.Empty;
        public FileGroupComparison HideoutIcon { get; set; } = FileGroupComparison.Empty;

        /// <summary>
        /// Every asset group, so the gate and the count below name them once rather than
        /// four times each, and a fifth group cannot be added to one and forgotten in the
        /// other.
        /// </summary>
        public IEnumerable<FileGroupComparison> AssetGroups
        {
            get
            {
                yield return MapSvg;
                yield return MarkerIcon;
                yield return ItemIcon;
                yield return HideoutIcon;
            }
        }

        /// <summary>
        /// The Assets mirror must be rewritten even when the database itself is
        /// unchanged, so a drifted mirror still counts as a publishable change.
        /// </summary>
        public bool MirrorNeedsRepair => Mirror == MirrorSyncState.Drifted;

        /// <summary>See <see cref="ManifestDriftReason"/>.</summary>
        public bool ManifestNeedsRepair => ManifestDriftReason != null;

        /// <summary>See <see cref="IndexDriftReason"/>.</summary>
        public bool IndexNeedsRepair => IndexDriftReason != null;

        /// <summary>
        /// Whether this publish has to rewrite the database endpoint documents: new data,
        /// a drifted Assets mirror, a manifest that no longer describes the database
        /// beside it, or a channel index that no longer names this endpoint. One
        /// expression, because the publish gate, the change count and the window all have
        /// to agree about it.
        /// <para>
        /// An index repair rides along here rather than counting on its own: the publish
        /// rewrites the index in the same step as the manifest, and folding it in keeps
        /// the gate, the count, the database section's icon and the confirm dialog saying
        /// the same thing. It costs nothing, because an index-only repair re-copies
        /// byte-identical bytes and <see cref="ResolvePublishVersion"/> still keeps the
        /// token while <see cref="DbChanged"/> is false.
        /// </para>
        /// </summary>
        public bool DbWillPublish => DbChanged || MirrorNeedsRepair || ManifestNeedsRepair || IndexNeedsRepair;

        public bool HasAnyChanges =>
            DbWillPublish || MapConfigsChanged || AssetGroups.Any(group => group.HasChanges);

        public int TotalChanges =>
            (DbWillPublish ? 1 : 0) +
            (MapConfigsChanged ? 1 : 0) +
            AssetGroups.Sum(group => group.ChangeCount);

        /// <summary>
        /// The token a publish actually writes, given the one the operator typed. The
        /// version and the manifest describe the DATABASE payload, and a client decides
        /// whether to download a multi-megabyte file by comparing this token alone, so
        /// bumping it for a map-config or icon publish would make every install in the
        /// field re-download a byte-identical database. The requested token is used only
        /// when the database really is being replaced, or when the endpoint has no token
        /// to keep in either of its documents: reverting to the operator's suggestion
        /// while the manifest still names a token would move the channel's version
        /// history backwards and re-issue tokens clients have already seen.
        /// </summary>
        public string ResolvePublishVersion(string requestedVersion) =>
            DbChanged ? requestedVersion : PublishedVersion ?? requestedVersion;

        /// <summary>
        /// Whether the operator can choose to republish these bytes under the token the
        /// channel already serves, i.e. whether the window offers "keep current version".
        /// <para>
        /// The database's bytes changing is not the same as its data changing: a commit
        /// records itself in the file header and can move pages around, so an editor
        /// session that writes and leaves the data as it found it (a re-import of
        /// unchanged cached data) still leaves a byte-different database holding the same
        /// rows. <see cref="DbChanged"/> compares bytes, so it says changed. A token
        /// bumped for that makes every install in the field download a multi-megabyte
        /// database it already has, because a client decides on the token alone. Passing
        /// <see cref="PublishedVersion"/> to
        /// <see cref="DataPublishService.PublishAsync"/> republishes the bytes and leaves
        /// the token where it is.
        /// </para>
        /// <para>
        /// Offered only when the database really is being replaced and the endpoint has a
        /// token to keep: with <see cref="DbChanged"/> false the token is kept anyway
        /// (see <see cref="ResolvePublishVersion"/>), and with nothing published anywhere
        /// there is no token to keep, so the operator's own suggestion is all there is.
        /// </para>
        /// </summary>
        public bool CanKeepPublishedVersion => DbChanged && PublishedVersion != null;
    }

    public class FileChangeInfo
    {
        public string FileName { get; set; } = "";
        public string SourcePath { get; set; } = "";
        public string TargetPath { get; set; } = "";
        public ChangeType Type { get; set; }
        public long SourceSize { get; set; }
        public long TargetSize { get; set; }
    }

    public enum ChangeType
    {
        Added,
        Updated,
        Unchanged
    }

    /// <summary>
    /// One asset group's comparison: the files a publish would copy, and how the whole
    /// group counted out. Returned by <see cref="CompareFilesAsync"/> rather than written
    /// back through callbacks, because the counts and the list they describe are one
    /// answer and a method that cannot return its own answer invites pairing one group's
    /// list with another group's counts.
    /// </summary>
    /// <param name="Changes">
    /// The added and updated files, in no particular order (they are surveyed in
    /// parallel). Unchanged files are counted but deliberately left out: this list is what
    /// a publish copies from, and the icon groups run to thousands of files that have not
    /// moved.
    /// </param>
    public sealed record FileGroupComparison(
        IReadOnlyList<FileChangeInfo> Changes, int Added, int Updated, int Unchanged)
    {
        /// <summary>A group with nothing to survey, i.e. one whose source folder is not there.</summary>
        public static readonly FileGroupComparison Empty =
            new(Array.Empty<FileChangeInfo>(), Added: 0, Updated: 0, Unchanged: 0);

        /// <summary>How many files this group contributes to the publish.</summary>
        public int ChangeCount => Added + Updated;

        /// <summary>Whether this group has anything to publish at all.</summary>
        public bool HasChanges => ChangeCount > 0;

        /// <summary>Every file the group holds on the source side, changed or not.</summary>
        public int Total => Added + Updated + Unchanged;
    }

    /// <summary>
    /// One group of files that ships inside an app release: where it is built, where it is
    /// published, and which files belong to it. The four groups differ in nothing else, so
    /// they are four rows here rather than four near-identical methods, and a fifth is one
    /// row plus the field that holds its comparison.
    /// </summary>
    /// <param name="Label">Names the group in the window's progress line.</param>
    /// <param name="SourceRelativePath">Below the editor's build output.</param>
    /// <param name="TargetRelativePath">Below TarkovHelper/Assets.</param>
    /// <param name="Pattern">The group's file type, as a search pattern.</param>
    private sealed record AssetGroup(
        string Label, string SourceRelativePath, string TargetRelativePath, string Pattern);

    private static readonly AssetGroup MapSvgGroup = new(
        "map SVG files", Path.Combine("Resources", "Maps"), Path.Combine("DB", "Maps"), "*.svg");

    private static readonly AssetGroup MarkerIconGroup = new(
        "marker icons", Path.Combine("Resources", "Icons"), Path.Combine("DB", "Icons"), "*.webp");

    private static readonly AssetGroup ItemIconGroup = new(
        "item icons", Path.Combine("wiki_data", "icons"), "icons", "*.png");

    private static readonly AssetGroup HideoutIconGroup = new(
        "hideout icons", Path.Combine("icons", "hideout"), Path.Combine("icons", "hideout"), "*.png");

    /// <summary>Where a group is built, i.e. what a comparison reads from.</summary>
    private string SourceDirFor(AssetGroup group) =>
        Path.Combine(_sourceBasePath, group.SourceRelativePath);

    /// <summary>Where a group is published, i.e. what a comparison and a publish write against.</summary>
    private string TargetDirFor(AssetGroup group) =>
        Path.Combine(_targetBasePath, group.TargetRelativePath);

    public class PublishResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }

        public int FilesCopied { get; set; }
        public int IconsCopied { get; set; }
        public string? NewVersion { get; set; }
        public List<string> CopiedFiles { get; set; } = new();
        public List<string> Errors { get; set; } = new();
    }

    /// <summary>
    /// Compare the source (TarkovDBEditor build output) against everything a publish
    /// would write: the live data-channel endpoint for the database, its manifest and
    /// version stamp, and TarkovHelper/Assets for the mirror and for everything that
    /// ships inside app releases.
    ///
    /// This is a read-only survey. Nothing here writes to the repository or to the source
    /// database, so opening the publish window, or refreshing it, cannot itself become a
    /// change to publish.
    /// </summary>
    public async Task<ComparisonResult> CompareAsync(Action<string>? progress = null)
    {
        var result = new ComparisonResult { Success = true };

        try
        {
            // Verify paths exist
            if (!Directory.Exists(_sourceBasePath))
            {
                result.Success = false;
                result.ErrorMessage = $"Source path not found: {_sourceBasePath}";
                return result;
            }

            if (!Directory.Exists(_targetBasePath))
            {
                result.Success = false;
                result.ErrorMessage = $"Target path not found: {_targetBasePath}\n\nPlease ensure TarkovHelper project exists.";
                return result;
            }

            // The channel is where the database is published; without it there is no
            // correct target, so this fails rather than silently writing Assets only.
            result.LiveDataFormatVersion = GetLiveDataFormatVersion();
            if (result.LiveDataFormatVersion == 0)
            {
                result.Success = false;
                result.ErrorMessage =
                    $"No data channel found under {_dataChannelPath}.\n\n" +
                    "Expected at least one data/v<N> directory holding tarkov_data.db and " +
                    "db_version.txt. Creating one is a deliberate change made together with " +
                    "the app-side format bump, not something this tool does.";
                return result;
            }
            result.ChannelDirPath = ChannelDirFor(result.LiveDataFormatVersion);
            result.MirrorsToAssets = result.LiveDataFormatVersion == MirroredDataFormatVersion;

            // 1. Compare Database. Reads the source's data format stamp too: the publish
            //    rewrites that stamp, so it is part of what the endpoint will receive.
            progress?.Invoke("Comparing database...");
            if (!await CompareDatabase(result))
            {
                return result;
            }

            // 2. Read current version
            progress?.Invoke("Reading version info...");
            await ReadVersionInfo(result);

            // 3. Check the endpoint pair, the manifest and the channel index. All three
            //    are independent surveys of the repository, so none depends on the order
            //    the others ran in.
            progress?.Invoke("Checking endpoints...");
            await CheckMirrorAsync(result);
            await CheckManifestAsync(result);
            await CheckIndexAsync(result);

            // 4. Compare map configs
            progress?.Invoke("Comparing map configs...");
            await CompareMapConfigs(result);

            // 5. Compare the asset groups that ship inside app releases. The same survey
            //    four times over, differing only in where each group lives and which files
            //    belong to it, so the groups themselves say all four differences.
            result.MapSvg = await CompareFilesAsync(MapSvgGroup, progress);
            result.MarkerIcon = await CompareFilesAsync(MarkerIconGroup, progress);
            result.ItemIcon = await CompareFilesAsync(ItemIconGroup, progress);
            result.HideoutIcon = await CompareFilesAsync(HideoutIconGroup, progress);

            progress?.Invoke("Comparison complete.");
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Reads the data format a database declares in its own header, using SQLite's
    /// user_version: the 32-bit slot SQLite reserves for the application and never reads
    /// itself. Opened read-only, because a comparison must not be able to modify the
    /// build output it is only reporting on.
    /// </summary>
    private static async Task<int> ReadDataFormatStampAsync(string databasePath)
    {
        try
        {
            await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version";
            return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }
        finally
        {
            // Pooled connections keep the file open, and it is hashed and copied next.
            SqliteConnection.ClearAllPools();
        }
    }

    /// <summary>
    /// Runs the publish constraints over a candidate database file, and returns the refusal
    /// the operator should see, or null when the file holds nothing a build in the field
    /// would read wrongly.
    /// <para>
    /// This is the last gate a byte passes. The refresh checks the same rules over the rows
    /// it built (<c>RefreshGuards.AssertPublishConstraints</c>), but the build phase is not
    /// the only way a row reaches the file: a hand edit in the editor's DataGrid, a
    /// <c>BsgIdBackfillService</c> run, or any correction made after a refresh writes
    /// straight to the database, and <c>DatabaseService</c>'s generic UPDATE can rewrite
    /// <c>Quests.Name</c> without <c>NormalizedName</c>. That desynchronization un-keys the
    /// quest's recorded progress in every install, silently, and cannot be repaired after
    /// the fact, because installs poll every five minutes and install what they download
    /// without checking it. So this refuses rather than warns, and there is no override.
    /// </para>
    /// <para>
    /// Read-only, like every other look this class takes at a database, and tolerant of a
    /// candidate published before a column existed: the rules are stated over the values the
    /// app reads, not the columns that hold them (see <see cref="PublishConstraints"/>).
    /// </para>
    /// </summary>
    private static async Task<string?> DescribeUnpublishableDataAsync(string databasePath)
    {
        if (!File.Exists(databasePath)) return null;

        PublishConstraints.Candidate candidate;
        try
        {
            candidate = await PublishConstraints.ReadAsync(databasePath);
        }
        catch (SqliteException ex)
        {
            // A candidate SQLite cannot read is not a candidate anyone should publish, and
            // the reasons differ enough that one catch-all would send the operator to the
            // wrong remedy.
            return DescribeSqliteFailure(databasePath, ex);
        }

        var problems = PublishConstraints.Problems(candidate);
        if (problems.Count == 0) return null;

        return PublishConstraints.Describe(
                $"{databasePath} holds data the builds in the field cannot read correctly",
                problems)
            + "\n\nEvery install downloads what this publishes and applies it without checking it, "
            + "so this stops here. Correct the rows named above in TarkovDBEditor, or re-run the "
            + "refresh, and compare again.";
    }

    /// <summary>
    /// Writes the live data format version into a database's own header, whether that is
    /// the build output about to be copied to the endpoint or the endpoint copy itself
    /// when there is no build output to publish from. A published database then carries
    /// its contract with it, so a client can check what it downloaded against what it can
    /// read without having to trust the manifest that came alongside, and every published
    /// database is stamped before it is hashed.
    ///
    /// Writes only when the stamp is not already the live one. SQLite commits by bumping
    /// the file change counter in the header, so re-stamping a database that already
    /// carries the right value still changes its bytes, which would make the tool report
    /// the database it just published as changed again. Returns null on success, or the
    /// operator-facing reason the database cannot be stamped.
    /// </summary>
    private static async Task<string?> StampDataFormatAsync(string databasePath, int dataFormatVersion)
    {
        try
        {
            if (await ReadDataFormatStampAsync(databasePath) == dataFormatVersion) return null;

            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                // PRAGMA takes no parameters; the value is an int this tool derived from
                // the repository layout, never user input.
                command.CommandText =
                    $"PRAGMA user_version = {dataFormatVersion.ToString(CultureInfo.InvariantCulture)}";
                await command.ExecuteNonQueryAsync();
            }

            return null;
        }
        catch (SqliteException ex)
        {
            return DescribeSqliteFailure(databasePath, ex);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    /// <summary>
    /// Turns a SQLite failure into a message that names what actually went wrong. The
    /// remedies are opposites, so one catch-all would send the operator to rebuild a
    /// perfectly good database because another window happened to be holding it open.
    /// </summary>
    private static string DescribeSqliteFailure(string databasePath, SqliteException ex) =>
        ex.SqliteErrorCode switch
        {
            // SQLITE_BUSY / SQLITE_LOCKED: another connection holds the file. The editor
            // itself is one, since it opens this very database from its build output.
            5 or 6 =>
                $"{databasePath} is in use by another connection ({ex.Message}).\n\n" +
                "Close the editor's other database windows and any external SQLite tool, then retry.",
            // SQLITE_PERM / SQLITE_READONLY / SQLITE_CANTOPEN: the file is there but this
            // process may not open it the way it needs to.
            3 or 8 or 14 =>
                $"{databasePath} cannot be opened ({ex.Message}).\n\n" +
                "Clear the file's read-only attribute, check the folder's permissions, and retry.",
            // SQLITE_CORRUPT / SQLITE_NOTADB: the file is not a database. Publishing it
            // anyway would ship whatever it actually is to every install.
            11 or 26 =>
                $"{databasePath} is not a database SQLite can open ({ex.Message}).\n\n" +
                "Rebuild the database in TarkovDBEditor before publishing.",
            _ =>
                $"SQLite refused {databasePath} (error {ex.SqliteErrorCode}: {ex.Message}).\n\n" +
                "Resolve the error above before publishing.",
        };

    /// <summary>
    /// Why there is no database for a publish to work from, or null when either end has
    /// one. A publish still has to be able to rewrite the manifest and the stamps of a
    /// channel that already holds a database; with no database at either end there is
    /// nothing to describe, and every later step (the manifest digest above all) would be
    /// hashing a file that does not exist. Shared by the comparison and the publish so
    /// both refuse on the same rule and say the same thing about it.
    /// </summary>
    private static string? DescribeMissingDatabase(string sourceDbPath, string channelDbPath)
    {
        if (File.Exists(sourceDbPath) || File.Exists(channelDbPath)) return null;

        return
            $"No database to publish.\n\n" +
            $"{sourceDbPath} is missing, and the endpoint {channelDbPath} has none either.\n\n" +
            "Build the database in TarkovDBEditor before publishing.";
    }

    /// <summary>
    /// Whether a version token is one the app can actually read off this channel.
    /// <para>
    /// Deliberately the same allowlist as <c>DataChannel.IsBareVersionToken</c>,
    /// the app's own reader: a token outside it makes that reader reject the whole
    /// manifest, so every install stops updating and CI goes red, while this tool would
    /// otherwise report the channel it just wrote as healthy and leave the operator no
    /// way to clear it. The two copies exist because the projects do not reference each
    /// other; they must be changed together, and
    /// <c>A_token_the_editor_publishes_is_a_token_the_app_can_read</c> fails if they
    /// drift apart.
    /// </para>
    /// <para>
    /// The permitted set is semver's own version grammar (<c>[0-9A-Za-z.-]</c> plus
    /// <c>+</c> for build metadata) with <c>_</c>. An allowlist rather than a check for
    /// the separators that break the bookmark, so a separator nobody thought of cannot
    /// walk around it.
    /// </para>
    /// </summary>
    private static bool IsPublishableVersionToken(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;

        foreach (var c in version)
        {
            var allowed = (c >= 'a' && c <= 'z')
                || (c >= 'A' && c <= 'Z')
                || (c >= '0' && c <= '9')
                || c is '.' or '-' or '_' or '+';

            if (!allowed) return false;
        }

        return true;
    }

    /// <summary>
    /// Hashes the source database and compares it against the live endpoint. Returns
    /// false when the comparison cannot be completed, having set the failure on
    /// <paramref name="result"/>.
    /// </summary>
    private async Task<bool> CompareDatabase(ComparisonResult result)
    {
        var sourceDbPath = Path.Combine(_sourceBasePath, DatabaseFileName);
        var targetDbPath = Path.Combine(result.ChannelDirPath!, DatabaseFileName);

        result.DbExists = File.Exists(sourceDbPath);

        // Refused here rather than half-applying a publish that cannot finish.
        var missingDatabase = DescribeMissingDatabase(sourceDbPath, targetDbPath);
        if (missingDatabase != null)
        {
            result.Success = false;
            result.ErrorMessage = missingDatabase;
            return false;
        }

        // Nothing to hash or stamp, but the endpoint holds a database whose documents a
        // publish can still repair.
        if (!result.DbExists) return true;

        try
        {
            result.SourceDataFormatStamp = await ReadDataFormatStampAsync(sourceDbPath);
        }
        catch (SqliteException ex)
        {
            result.Success = false;
            result.ErrorMessage = DescribeSqliteFailure(sourceDbPath, ex);
            return false;
        }

        // The preflight: what the operator is about to publish is checked against the rules
        // every installed build reads by, before the window offers a Publish button at all.
        // PublishDatabaseAsync checks the file it is actually copying as well, because a
        // comparison can go stale between the two.
        var unpublishable = await DescribeUnpublishableDataAsync(sourceDbPath);
        if (unpublishable != null)
        {
            result.Success = false;
            result.ErrorMessage = unpublishable;
            return false;
        }

        result.SourceDbHash = await ComputeFileHashAsync(sourceDbPath);
        result.SourceDbSize = new FileInfo(sourceDbPath).Length;

        if (File.Exists(targetDbPath))
        {
            result.TargetDbHash = await ComputeFileHashAsync(targetDbPath);
            result.TargetDbSize = new FileInfo(targetDbPath).Length;
            // The publish stamps the source with the live data format, so what lands on
            // the endpoint is the stamped file rather than the bytes on disk right now. A
            // stamp that is not yet the live one is therefore a pending change in its own
            // right, even in the corner where the two hashes happen to agree.
            result.DbChanged = result.SourceDbHash != result.TargetDbHash
                               || result.SourceDataFormatStamp != result.LiveDataFormatVersion;
        }
        else
        {
            result.DbChanged = true; // New file
        }

        return true;
    }

    /// <summary>
    /// Reads the token the channel endpoint publishes. Reading only: the token to publish
    /// next follows from this one, so <see cref="ComparisonResult.NewVersion"/> derives it
    /// rather than storing a second copy that could disagree.
    /// </summary>
    private async Task ReadVersionInfo(ComparisonResult result)
    {
        var versionPath = Path.Combine(result.ChannelDirPath!, VersionFileName);

        if (!File.Exists(versionPath)) return;

        // First non-blank line only. The token is the whole document; reading it this
        // way just tolerates the trailing newline and stray whitespace a hand edit
        // leaves behind, so neither becomes part of the token.
        var lines = await File.ReadAllLinesAsync(versionPath);
        result.CurrentVersion = lines
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0);
    }

    /// <summary>
    /// Compares both files the two format-1 endpoints serve, in one place. Owning the
    /// whole answer matters: split across two steps, whichever ran last decided it, and
    /// reordering them would silently report a drifted mirror as in sync.
    /// </summary>
    private async Task CheckMirrorAsync(ComparisonResult result)
    {
        // Not applicable only when this format has no mirror. Both files compared here
        // belong to the repository, so the build output having no database of its own
        // does not make the pair unjudgeable: a publish repairs the mirror from the
        // channel endpoint in that case, which is why a drift found here is always
        // something the tool can clear.
        if (!result.MirrorsToAssets)
        {
            result.Mirror = MirrorSyncState.NotApplicable;
            return;
        }

        var channelDir = result.ChannelDirPath!;
        // The stamps count as much as the database: a version-only drift would otherwise
        // leave the tool with nothing to publish while the two endpoints answered
        // differently about the same bytes.
        var inSync =
            await FilesMatchAsync(
                Path.Combine(channelDir, DatabaseFileName), Path.Combine(_targetBasePath, DatabaseFileName))
            && await FilesMatchAsync(
                Path.Combine(channelDir, VersionFileName), Path.Combine(_targetBasePath, VersionFileName));

        result.Mirror = inSync
            ? MirrorSyncState.InSync
            : MirrorSyncState.Drifted;
    }

    /// <summary>Whether two files exist and hold the same bytes. A missing side is a mismatch.</summary>
    private async Task<bool> FilesMatchAsync(string left, string right)
    {
        if (!File.Exists(left) || !File.Exists(right)) return false;

        return await ComputeFileHashAsync(left) == await ComputeFileHashAsync(right);
    }

    /// <summary>
    /// Checks that the channel manifest still describes the database beside it, including
    /// what that database says about itself: the data format it stamps into its own
    /// header has to be the one the manifest declares, or every client refuses the
    /// payload as one no publish produced. CI asserts exactly this about the committed
    /// repository, so without the check here a drifted endpoint would be a red build with
    /// the Publish button disabled and no way to clear it from the editor.
    ///
    /// Runs after <c>ReadVersionInfo</c>, whose token the manifest has to agree with.
    /// </summary>
    private async Task CheckManifestAsync(ComparisonResult result)
    {
        var channelDir = result.ChannelDirPath!;
        var databasePath = Path.Combine(channelDir, DatabaseFileName);
        var manifestPath = Path.Combine(channelDir, ManifestFileName);
        var shown = $"{DataChannelDirName}/v{result.LiveDataFormatVersion}/{ManifestFileName}";

        // Nothing published yet at this endpoint. The publish writes the database and its
        // manifest together, so there is no drift to report against a file that is about
        // to be created.
        if (!File.Exists(databasePath)) return;

        if (!File.Exists(manifestPath))
        {
            result.ManifestDriftReason = $"{shown} is missing";
            return;
        }

        ChannelManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ChannelManifest>(
                await File.ReadAllTextAsync(manifestPath), ChannelDocumentReadOptions);
        }
        catch (JsonException)
        {
            manifest = null;
        }

        // Recorded before the checks below, and even from a manifest that fails them: it
        // is the token this endpoint last told clients about, and a publish keeps it
        // rather than restarting the version history when db_version.txt has gone missing.
        result.ManifestVersion = string.IsNullOrWhiteSpace(manifest?.Version) ? null : manifest.Version.Trim();

        if (manifest?.Database?.File == null)
        {
            result.ManifestDriftReason = $"{shown} cannot be read as a channel manifest";
            return;
        }

        if (manifest.SchemaVersion != ManifestSchemaVersion)
        {
            // A document shape this tool does not write is one no current client is
            // promised to read, and CI rejects it. Republishing restores the shape.
            result.ManifestDriftReason =
                $"{shown} declares document schema {manifest.SchemaVersion}, not {ManifestSchemaVersion}";
            return;
        }

        if (manifest.DataFormatVersion != result.LiveDataFormatVersion)
        {
            result.ManifestDriftReason =
                $"{shown} declares data format {manifest.DataFormatVersion}, not {result.LiveDataFormatVersion}";
            return;
        }

        // The database has to make the same claim in its own header, because that is the
        // one clients check. A stamp of 0 is not "format 0" but "this file makes no
        // claim", and the app refuses such a payload outright, so an endpoint populated by
        // hand would otherwise stop every install in the field with nothing here to
        // publish. A publish stamps the endpoint copy, which is what clears this.
        var shownDatabase = $"{DataChannelDirName}/v{result.LiveDataFormatVersion}/{DatabaseFileName}";
        int endpointStamp;
        try
        {
            endpointStamp = await ReadDataFormatStampAsync(databasePath);
        }
        catch (SqliteException ex)
        {
            result.ManifestDriftReason = $"{shownDatabase} cannot be read as a database ({ex.Message})";
            return;
        }

        if (endpointStamp != result.LiveDataFormatVersion)
        {
            result.ManifestDriftReason = endpointStamp == 0
                ? $"{shownDatabase} carries no data format stamp, so clients refuse it"
                : $"{shownDatabase} is stamped data format {endpointStamp}, not {result.LiveDataFormatVersion}";
            return;
        }

        if (!string.Equals(manifest.Database.File, DatabaseFileName, StringComparison.Ordinal))
        {
            result.ManifestDriftReason = $"{shown} names {manifest.Database.File}, not {DatabaseFileName}";
            return;
        }

        var actualSize = new FileInfo(databasePath).Length;
        if (manifest.Database.Size != actualSize)
        {
            result.ManifestDriftReason =
                $"{shown} records size {manifest.Database.Size}, but the database is {actualSize} bytes";
            return;
        }

        if (string.IsNullOrWhiteSpace(manifest.Database.Digest))
        {
            // Shipping without a digest silently turns off download verification for every
            // client, which is worse than any of the mismatches below.
            result.ManifestDriftReason = $"{shown} records no digest, so clients cannot verify a download";
            return;
        }

        var actualDigest = $"sha256:{await ComputeFileSha256Async(databasePath)}";
        if (!string.Equals(manifest.Database.Digest, actualDigest, StringComparison.OrdinalIgnoreCase))
        {
            result.ManifestDriftReason = $"{shown} records a digest the database beside it does not match";
            return;
        }

        if (result.ManifestVersion == null)
        {
            // Checked for presence rather than only against db_version.txt: two absent
            // tokens compare equal, and the app's own reader rejects a manifest with no
            // version outright, so a healthy report here would leave every install unable
            // to read the endpoint at all.
            result.ManifestDriftReason = $"{shown} records no version, so clients cannot read it";
            return;
        }

        if (!IsPublishableVersionToken(result.ManifestVersion))
        {
            // Checked before the two tokens are compared, because a db_version.txt
            // holding the same unreadable token compares equal and would report the
            // endpoint healthy. The app's reader rejects the whole manifest over this
            // (see IsPublishableVersionToken), so the channel serves nothing readable
            // until a publish replaces the token.
            result.ManifestDriftReason =
                $"{shown} records version {result.ManifestVersion}, which no client can read";
            return;
        }

        // The bookmark a fresh install ships with has to name the version the manifest
        // does, or every install re-downloads forever or rejects a good database.
        if (!string.Equals(result.ManifestVersion, result.CurrentVersion, StringComparison.Ordinal))
        {
            result.ManifestDriftReason =
                $"{shown} records version {result.ManifestVersion}, " +
                $"but {VersionFileName} says {result.CurrentVersion ?? "(none)"}";
        }
    }

    /// <summary>
    /// Case-insensitive to match the app's own channel-document reader: a manifest or
    /// index this tool accepts must be one the client accepts, and the reverse.
    /// </summary>
    private static readonly JsonSerializerOptions ChannelDocumentReadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Read-back shape of <see cref="ManifestFileName"/>, for drift detection only.</summary>
    private sealed record ChannelManifest(
        int SchemaVersion,
        int DataFormatVersion,
        string? Version,
        ChannelManifestDatabase? Database);

    private sealed record ChannelManifestDatabase(string? File, string? Digest, long Size);

    /// <summary>
    /// Checks that data/index.json still names the format this publish writes. The index
    /// is how a build pinned to a superseded format learns that nothing further is coming
    /// for it, and how a current build learns its endpoint is still published, so an
    /// index that names the wrong format (or cannot be read at all) is as broken as a
    /// drifted manifest. CI asserts the same thing about the committed repository, and
    /// without the check here that red build would come with the Publish button disabled
    /// and no way to clear it from the editor.
    /// <para>
    /// Compares the parsed fields rather than the rendered bytes: index.json is committed
    /// with CRLF and this repository has no .gitattributes, so a byte comparison would
    /// call a healthy index drifted on whichever machine checked out the other line
    /// ending. The field checks cost nothing and still catch a renamed pointer field,
    /// which deserializes to 0 and fails the format check.
    /// </para>
    /// </summary>
    private async Task CheckIndexAsync(ComparisonResult result)
    {
        var indexPath = Path.Combine(_dataChannelPath, IndexFileName);
        var shown = $"{DataChannelDirName}/{IndexFileName}";

        if (!File.Exists(indexPath))
        {
            result.IndexDriftReason = $"{shown} is missing";
            return;
        }

        ChannelIndex? index;
        try
        {
            index = JsonSerializer.Deserialize<ChannelIndex>(
                await File.ReadAllTextAsync(indexPath), ChannelDocumentReadOptions);
        }
        catch (JsonException)
        {
            index = null;
        }

        if (index == null)
        {
            result.IndexDriftReason = $"{shown} cannot be read as a channel index";
            return;
        }

        if (index.SchemaVersion != IndexSchemaVersion)
        {
            // A document shape this tool does not write is one no current client is
            // promised to read, and CI rejects it. Republishing restores the shape.
            result.IndexDriftReason =
                $"{shown} declares document schema {index.SchemaVersion}, not {IndexSchemaVersion}";
            return;
        }

        if (index.CurrentDataFormatVersion != result.LiveDataFormatVersion)
        {
            result.IndexDriftReason =
                $"{shown} publishes data format {index.CurrentDataFormatVersion}, " +
                $"not {result.LiveDataFormatVersion}";
        }
    }

    /// <summary>Read-back shape of <see cref="IndexFileName"/>, for drift detection only.</summary>
    private sealed record ChannelIndex(int SchemaVersion, int CurrentDataFormatVersion);

    private async Task CompareMapConfigs(ComparisonResult result)
    {
        var sourceConfigPath = Path.Combine(_sourceBasePath, "Resources", "Data", "map_configs.json");
        var targetConfigPath = Path.Combine(_targetBasePath, "DB", "Data", "map_configs.json");

        if (!File.Exists(sourceConfigPath))
        {
            return;
        }

        result.SourceMapConfigsHash = await ComputeFileHashAsync(sourceConfigPath);

        if (File.Exists(targetConfigPath))
        {
            result.TargetMapConfigsHash = await ComputeFileHashAsync(targetConfigPath);
            result.MapConfigsChanged = result.SourceMapConfigsHash != result.TargetMapConfigsHash;
        }
        else
        {
            result.MapConfigsChanged = true;
        }
    }

    /// <summary>
    /// Surveys one asset group: which of its files a publish would add or update, and how
    /// many it would leave alone. Reports the answer as its return value, so the group a
    /// count belongs to is decided by the call site rather than by a callback.
    /// <para>
    /// A group whose source folder is not there counts nothing. The build output only
    /// grows a group's folder once that group's producer has run, and an absent folder is
    /// nothing to publish rather than a failure. Files present only on the target side are
    /// likewise nothing: this tool copies, and never deletes what a previous release
    /// shipped.
    /// </para>
    /// </summary>
    private async Task<FileGroupComparison> CompareFilesAsync(AssetGroup group, Action<string>? progress)
    {
        progress?.Invoke($"Comparing {group.Label}...");

        var sourceDir = SourceDirFor(group);
        var targetDir = TargetDirFor(group);

        if (!Directory.Exists(sourceDir)) return FileGroupComparison.Empty;

        var sourceFiles = Directory.GetFiles(sourceDir, group.Pattern, SearchOption.TopDirectoryOnly);

        // Process files in parallel for better performance (especially for large icon folders)
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };
        var results = new System.Collections.Concurrent.ConcurrentBag<FileChangeInfo>();

        await Parallel.ForEachAsync(sourceFiles, parallelOptions, async (sourceFile, ct) =>
        {
            var fileName = Path.GetFileName(sourceFile);
            var targetFile = Path.Combine(targetDir, fileName);
            var sourceSize = new FileInfo(sourceFile).Length;

            var info = new FileChangeInfo
            {
                FileName = fileName,
                SourcePath = sourceFile,
                TargetPath = targetFile,
                SourceSize = sourceSize
            };

            if (!File.Exists(targetFile))
            {
                info.Type = ChangeType.Added;
            }
            else
            {
                info.TargetSize = new FileInfo(targetFile).Length;

                // Sizes first, and only then the hash: a file whose length moved is
                // different without reading either copy, which matters because the icon
                // groups run to thousands of files that have not moved at all.
                info.Type =
                    sourceSize != info.TargetSize
                    || await ComputeFileHashAsync(sourceFile) != await ComputeFileHashAsync(targetFile)
                        ? ChangeType.Updated
                        : ChangeType.Unchanged;
            }

            results.Add(info);
        });

        // Count results
        var changes = new List<FileChangeInfo>();
        int added = 0, updated = 0, unchanged = 0;
        foreach (var info in results)
        {
            switch (info.Type)
            {
                case ChangeType.Added:
                    added++;
                    changes.Add(info);
                    break;
                case ChangeType.Updated:
                    updated++;
                    changes.Add(info);
                    break;
                case ChangeType.Unchanged:
                    unchanged++;
                    // Don't add unchanged files to reduce list size
                    break;
            }
        }

        return new FileGroupComparison(changes, added, updated, unchanged);
    }

    /// <summary>
    /// Publish the changes a comparison found: the database and its documents to the live
    /// channel endpoint (mirrored into TarkovHelper/Assets while format 1 is live), and
    /// everything that ships inside app releases to Assets.
    ///
    /// <paramref name="requestedVersion"/> is the token the operator typed. It is used
    /// only when the database itself is being replaced; see
    /// <see cref="ComparisonResult.ResolvePublishVersion"/>. The token actually written
    /// comes back as <see cref="PublishResult.NewVersion"/>.
    /// </summary>
    public async Task<PublishResult> PublishAsync(ComparisonResult comparison, string requestedVersion, Action<string>? progress = null)
    {
        var newVersion = comparison.ResolvePublishVersion(requestedVersion);
        var result = new PublishResult { Success = true, NewVersion = newVersion };

        try
        {
            if (comparison.ChannelDirPath == null)
            {
                Fail(result, "Comparison did not resolve a data channel; re-run the comparison.");
                return result;
            }

            // Refuse before anything is written, so a token the app cannot read never
            // reaches the manifest. The app's reader rejects the whole document over one
            // (see IsPublishableVersionToken), which stops every install updating and
            // turns CI red, and the tool would then be reporting a channel it had just
            // written as healthy with nothing left to publish.
            if (!IsPublishableVersionToken(newVersion))
            {
                Fail(
                    result,
                    $"\"{newVersion}\" is not a version token this channel can publish.\n\n" +
                    "Use letters, digits and . - _ + only, for example 1.0.11. The token is " +
                    "written verbatim into the manifest and db_version.txt, and every " +
                    "install refuses a manifest whose token carries anything else.");
                return result;
            }

            // 1. Put the stamped database on the channel endpoint, and the same bytes in
            //    the Assets mirror while format 1 is live.
            if (!await PublishDatabaseAsync(comparison, result, progress))
            {
                return result;
            }

            // 2. Copy map configs
            if (comparison.MapConfigsChanged)
            {
                progress?.Invoke("Copying map configs...");
                var sourceConfigPath = Path.Combine(_sourceBasePath, "Resources", "Data", "map_configs.json");
                var targetConfigPath = Path.Combine(_targetBasePath, "DB", "Data", "map_configs.json");

                Directory.CreateDirectory(Path.GetDirectoryName(targetConfigPath)!);
                File.Copy(sourceConfigPath, targetConfigPath, overwrite: true);
                result.CopiedFiles.Add("DB/Data/map_configs.json");
                result.FilesCopied++;
            }

            // 3. Copy map SVGs. The target folder comes from the same group definition the
            //    comparison read, so the two cannot describe different places.
            progress?.Invoke("Copying map SVGs...");
            Directory.CreateDirectory(TargetDirFor(MapSvgGroup));
            foreach (var change in comparison.MapSvg.Changes.Where(c => c.Type != ChangeType.Unchanged))
            {
                File.Copy(change.SourcePath, change.TargetPath, overwrite: true);
                result.CopiedFiles.Add($"DB/Maps/{change.FileName}");
                result.FilesCopied++;
            }

            // 4. Copy marker icons
            progress?.Invoke("Copying marker icons...");
            Directory.CreateDirectory(TargetDirFor(MarkerIconGroup));
            foreach (var change in comparison.MarkerIcon.Changes.Where(c => c.Type != ChangeType.Unchanged))
            {
                File.Copy(change.SourcePath, change.TargetPath, overwrite: true);
                result.IconsCopied++;
            }

            // 5. Copy item icons
            progress?.Invoke($"Copying item icons ({comparison.ItemIcon.Changes.Count} changes)...");
            Directory.CreateDirectory(TargetDirFor(ItemIconGroup));
            int iconCount = 0;
            foreach (var change in comparison.ItemIcon.Changes.Where(c => c.Type != ChangeType.Unchanged))
            {
                File.Copy(change.SourcePath, change.TargetPath, overwrite: true);
                result.IconsCopied++;
                iconCount++;

                if (iconCount % 100 == 0)
                {
                    progress?.Invoke($"Copying item icons ({iconCount}/{comparison.ItemIcon.Changes.Count})...");
                }
            }

            // 6. Copy hideout icons
            progress?.Invoke("Copying hideout icons...");
            Directory.CreateDirectory(TargetDirFor(HideoutIconGroup));
            foreach (var change in comparison.HideoutIcon.Changes.Where(c => c.Type != ChangeType.Unchanged))
            {
                File.Copy(change.SourcePath, change.TargetPath, overwrite: true);
                result.IconsCopied++;
            }

            // 7. Write the documents that describe the database last, after the database
            //    itself: an interrupted publish then leaves the old version pointing at
            //    data that is merely newer, never a new version (or hash) pointing at
            //    data that never arrived.
            await PublishEndpointDocumentsAsync(comparison, newVersion, result, progress);

            progress?.Invoke("Publish complete.");
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            result.Errors.Add(ex.Message);
        }

        return result;
    }

    /// <summary>
    /// Records a refusal on <paramref name="result"/>: not successful, why, and the same
    /// why in the list the window shows beneath it. Returns false, so a step that refuses
    /// reads as one statement at every site and cannot report half of a failure.
    /// </summary>
    private static bool Fail(PublishResult result, string message)
    {
        result.Success = false;
        result.ErrorMessage = message;
        result.Errors.Add(message);
        return false;
    }

    /// <summary>
    /// Puts the stamped database on the channel endpoint, and the same bytes in the
    /// Assets mirror while format 1 is live (streamed, to handle files open by other
    /// processes). Whichever branch runs, this leaves the endpoint database stamped with
    /// the live data format before the manifest hashes it: the app refuses a payload that
    /// carries no stamp, on the stated grounds that every publish writes one.
    /// <para>
    /// Returns false when the publish must stop, having recorded why. Every refusal here
    /// happens before the first byte is written, so a tree that fails this step is exactly
    /// the tree that entered it.
    /// </para>
    /// </summary>
    private async Task<bool> PublishDatabaseAsync(
        ComparisonResult comparison, PublishResult result, Action<string>? progress)
    {
        var sourceDbPath = Path.Combine(_sourceBasePath, DatabaseFileName);
        var channelDbPath = Path.Combine(comparison.ChannelDirPath!, DatabaseFileName);

        // Refuse before anything is copied. The manifest written at the end describes the
        // endpoint database, so a publish that can neither copy one nor find one already
        // there cannot finish, and finding that out at the manifest step would leave the
        // tree half-published.
        var missingDatabase = DescribeMissingDatabase(sourceDbPath, channelDbPath);
        if (missingDatabase != null) return Fail(result, missingDatabase);

        // Which branch runs is decided by the filesystem now, as the refusal above was,
        // rather than by the comparison's DbExists: a comparison gone stale must not send
        // this step reaching for a database that is not there.
        var sourceExists = File.Exists(sourceDbPath);
        if (sourceExists && comparison.DbWillPublish)
        {
            // New data, a drifted mirror or a drifted endpoint document, with a build
            // output to publish from. Copied even when the bytes are unchanged, so one
            // publish always leaves both endpoints byte-identical and described.
            //
            // Checked here as well as in the comparison, against the file this step is
            // actually about to copy: the comparison can be minutes old, and the editor can
            // write to its own build output in between. Nothing has been copied yet, so the
            // tree is exactly as it was.
            progress?.Invoke("Checking publish constraints...");
            var unpublishable = await DescribeUnpublishableDataAsync(sourceDbPath);
            if (unpublishable != null) return Fail(result, unpublishable);

            // Stamp here, not during the comparison: this is the last moment before the
            // bytes are read, both endpoints receive the one stamped file, and a
            // comparison stays a read-only survey of the repository.
            progress?.Invoke("Stamping data format...");
            var stampFailure = await StampDataFormatAsync(sourceDbPath, comparison.LiveDataFormatVersion);
            // Nothing has been copied yet, so the tree is exactly as it was.
            if (stampFailure != null) return Fail(result, stampFailure);

            progress?.Invoke("Copying database...");
            Directory.CreateDirectory(comparison.ChannelDirPath!);
            await CopyFileWithShareAsync(sourceDbPath, channelDbPath);
            result.CopiedFiles.Add($"{DataChannelDirName}/v{comparison.LiveDataFormatVersion}/{DatabaseFileName}");
            result.FilesCopied++;

            if (comparison.MirrorsToAssets)
            {
                await CopyFileWithShareAsync(sourceDbPath, Path.Combine(_targetBasePath, DatabaseFileName));
                result.CopiedFiles.Add(DatabaseFileName);
                result.FilesCopied++;
            }
        }
        else if (!sourceExists)
        {
            // No build output to publish from, so the endpoint copy is the database this
            // publish describes (the refusal above guarantees it is there): it is the one
            // that has to be stamped, and the one the mirror is repaired from. The mirror
            // is rewritten rather than only when it drifted, because stamping changes the
            // endpoint's bytes and the documents go on to give both copies the same
            // version token.
            //
            // Checked first even though these bytes are already on the channel: the Assets
            // mirror is an endpoint pre-channel builds poll, so this step can still be the
            // moment unreadable data reaches an install.
            progress?.Invoke("Checking publish constraints...");
            var unpublishableEndpoint = await DescribeUnpublishableDataAsync(channelDbPath);
            if (unpublishableEndpoint != null) return Fail(result, unpublishableEndpoint);

            progress?.Invoke("Stamping data format...");
            var stampFailure = await StampDataFormatAsync(channelDbPath, comparison.LiveDataFormatVersion);
            if (stampFailure != null) return Fail(result, stampFailure);

            if (comparison.MirrorsToAssets)
            {
                progress?.Invoke("Copying database...");
                await CopyFileWithShareAsync(channelDbPath, Path.Combine(_targetBasePath, DatabaseFileName));
                result.CopiedFiles.Add(DatabaseFileName);
                result.FilesCopied++;
            }
        }

        return true;
    }

    /// <summary>
    /// Writes every document that describes the published database: the channel manifest,
    /// the version stamp on both endpoints, and the channel index above them.
    /// <para>
    /// <paramref name="newVersion"/> is the resolved token, so a publish that carried only
    /// icons or map configs rewrites these documents with the token they already had and
    /// no install is told to fetch a database it already has.
    /// </para>
    /// </summary>
    private async Task PublishEndpointDocumentsAsync(
        ComparisonResult comparison, string newVersion, PublishResult result, Action<string>? progress)
    {
        progress?.Invoke("Updating manifest...");
        await WriteManifestAsync(comparison, newVersion, result);

        await File.WriteAllTextAsync(
            Path.Combine(comparison.ChannelDirPath!, VersionFileName), newVersion);
        result.CopiedFiles.Add($"{DataChannelDirName}/v{comparison.LiveDataFormatVersion}/{VersionFileName}");

        if (comparison.MirrorsToAssets)
        {
            // Safe to stamp the mirror with the channel's token: the database step either
            // put the channel's bytes there, or the comparison found the pair in sync and
            // left them alone. Writing this token onto bytes no publish put there would
            // leave a fresh install seeded from Assets bookmarked as up to date on a
            // database it never received, and it would never download again.
            await File.WriteAllTextAsync(
                Path.Combine(_targetBasePath, VersionFileName), newVersion);
            result.CopiedFiles.Add(VersionFileName);
        }

        // The channel index names the data format currently published. Rewritten every
        // time so it cannot drift, and it is the only mutable part of the channel:
        // superseded endpoint directories are never touched again, which is how a build
        // learns it was left behind without anyone hand-editing history.
        await WriteIndexAsync(comparison.LiveDataFormatVersion, result);
    }

    /// <summary>
    /// Writes data/v&lt;N&gt;/manifest.json: the document new builds read. The hash and size
    /// travel in the same document as the version so a client cannot pair a fresh
    /// version with a stale or truncated database, which per-file CDN caching otherwise
    /// allows.
    /// </summary>
    private async Task WriteManifestAsync(ComparisonResult comparison, string newVersion, PublishResult result)
    {
        // Guaranteed to exist: the publish refuses up front unless the database is either
        // on the endpoint already or copyable from the build output.
        var databasePath = Path.Combine(comparison.ChannelDirPath!, DatabaseFileName);

        var manifest = new
        {
            schemaVersion = ManifestSchemaVersion,
            dataFormatVersion = comparison.LiveDataFormatVersion,
            version = newVersion,
            database = new
            {
                file = DatabaseFileName,
                // Algorithm-qualified, following OCI: a reader that does not implement
                // the named algorithm can tell that apart from no digest at all.
                digest = $"sha256:{await ComputeFileSha256Async(databasePath)}",
                size = new FileInfo(databasePath).Length,
            },
        };

        var path = Path.Combine(comparison.ChannelDirPath!, ManifestFileName);
        await File.WriteAllTextAsync(path, ToJson(manifest));
        result.CopiedFiles.Add($"{DataChannelDirName}/v{comparison.LiveDataFormatVersion}/{ManifestFileName}");
        result.FilesCopied++;
    }

    /// <summary>
    /// Writes data/index.json, which names the data format version the project publishes right
    /// now. Builds pinned to an older format compare against it to learn that nothing
    /// further is coming for them.
    /// </summary>
    private async Task WriteIndexAsync(int liveDataFormat, PublishResult result)
    {
        var index = new { schemaVersion = IndexSchemaVersion, currentDataFormatVersion = liveDataFormat };

        await File.WriteAllTextAsync(Path.Combine(_dataChannelPath, IndexFileName), ToJson(index));
        result.CopiedFiles.Add($"{DataChannelDirName}/{IndexFileName}");
        result.FilesCopied++;
    }

    /// <summary>
    /// Indented, newline-terminated JSON: these files are reviewed in diffs by hand, so
    /// a one-line document would make every publish an unreadable change.
    /// </summary>
    private static string ToJson(object value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + "\n";

    private async Task<string> ComputeFileSha256Async(string filePath)
    {
        using var sha256 = SHA256.Create();
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(await sha256.ComputeHashAsync(stream)).ToLowerInvariant();
    }

    private async Task<string> ComputeFileHashAsync(string filePath)
    {
        using var md5 = MD5.Create();
        // Use FileShare.ReadWrite to allow reading files that are open by other processes (like SQLite DB)
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var hash = await md5.ComputeHashAsync(stream);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Copy a file using FileShare.ReadWrite to handle files that are open by other processes (like SQLite DB)
    /// </summary>
    private async Task CopyFileWithShareAsync(string sourcePath, string targetPath)
    {
        const int bufferSize = 81920; // 80KB buffer

        await using var sourceStream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        await using var targetStream = new FileStream(
            targetPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        await sourceStream.CopyToAsync(targetStream);
    }

    public void Dispose()
    {
        // Nothing to dispose
    }
}

/// <summary>
/// Whether the Assets mirror matches the channel endpoint, as one value rather than a
/// boolean that cannot tell "checked and identical" from "never checked". Drifted means
/// the repo is mid-skew (a half-published commit); publishing both copies fixes it,
/// which is why it is surfaced rather than blocking.
/// <para>
/// A namespace-scope type rather than one nested in
/// <see cref="DataPublishService.ComparisonResult"/>: every mention of a state is a
/// four-segment name otherwise, and the state describes the channel, not one report
/// about it.
/// </para>
/// </summary>
public enum MirrorSyncState
{
    /// <summary>This format has no Assets mirror.</summary>
    NotApplicable,
    InSync,
    Drifted,
}
