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
    private const int MirroredDataFormat = 1;

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
    public int GetLiveDataFormat()
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

        // Data channel
        /// <summary>Format this publish writes, i.e. the highest data/v&lt;N&gt; in the repo.</summary>
        public int LiveDataFormat { get; set; }
        public string? ChannelDirPath { get; set; }

        /// <summary>
        /// True while the live format is the one the pre-channel Assets endpoint also
        /// serves, so a publish must write both copies.
        /// </summary>
        public bool MirrorsToAssets { get; set; }

        /// <summary>
        /// Whether the Assets mirror currently matches the channel endpoint. False means
        /// the repo is mid-skew (a half-published commit); publishing both copies fixes
        /// it, which is why this is surfaced rather than blocking.
        /// </summary>
        public bool MirrorInSync { get; set; } = true;

        // Version
        public string? CurrentVersion { get; set; }
        public string? NewVersion { get; set; }

        // Map configs
        public bool MapConfigsChanged { get; set; }
        public string? SourceMapConfigsHash { get; set; }
        public string? TargetMapConfigsHash { get; set; }

        // Map SVGs
        public List<FileChangeInfo> MapSvgChanges { get; set; } = new();
        public int MapSvgAdded { get; set; }
        public int MapSvgUpdated { get; set; }
        public int MapSvgUnchanged { get; set; }

        // Map marker icons
        public List<FileChangeInfo> MarkerIconChanges { get; set; } = new();
        public int MarkerIconAdded { get; set; }
        public int MarkerIconUpdated { get; set; }
        public int MarkerIconUnchanged { get; set; }

        // Item icons
        public List<FileChangeInfo> ItemIconChanges { get; set; } = new();
        public int ItemIconAdded { get; set; }
        public int ItemIconUpdated { get; set; }
        public int ItemIconUnchanged { get; set; }

        // Hideout icons
        public List<FileChangeInfo> HideoutIconChanges { get; set; } = new();
        public int HideoutIconAdded { get; set; }
        public int HideoutIconUpdated { get; set; }
        public int HideoutIconUnchanged { get; set; }

        /// <summary>
        /// The Assets mirror must be rewritten even when the database itself is
        /// unchanged, so a drifted mirror still counts as a publishable change.
        /// </summary>
        public bool MirrorNeedsRepair => MirrorsToAssets && !MirrorInSync;

        public bool HasAnyChanges => DbChanged || MirrorNeedsRepair || MapConfigsChanged ||
            MapSvgAdded > 0 || MapSvgUpdated > 0 ||
            MarkerIconAdded > 0 || MarkerIconUpdated > 0 ||
            ItemIconAdded > 0 || ItemIconUpdated > 0 ||
            HideoutIconAdded > 0 || HideoutIconUpdated > 0;

        public int TotalChanges =>
            (DbChanged || MirrorNeedsRepair ? 1 : 0) +
            (MapConfigsChanged ? 1 : 0) +
            MapSvgAdded + MapSvgUpdated +
            MarkerIconAdded + MarkerIconUpdated +
            ItemIconAdded + ItemIconUpdated +
            HideoutIconAdded + HideoutIconUpdated;
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
    /// Compare all files between source (TarkovDBEditor Release) and target (TarkovHelper Assets)
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
            result.LiveDataFormat = GetLiveDataFormat();
            if (result.LiveDataFormat == 0)
            {
                result.Success = false;
                result.ErrorMessage =
                    $"No data channel found under {_dataChannelPath}.\n\n" +
                    "Expected at least one data/v<N> directory holding tarkov_data.db and " +
                    "db_version.txt. Creating one is a deliberate change made together with " +
                    "the app-side format bump, not something this tool does.";
                return result;
            }
            result.ChannelDirPath = ChannelDirFor(result.LiveDataFormat);
            result.MirrorsToAssets = result.LiveDataFormat == MirroredDataFormat;

            // Stamp the source before it is hashed or compared, so every downstream
            // number describes a database that declares its own data format. Done here
            // rather than after copying because both endpoints must end up byte
            // identical, which only holds if one stamped file is copied to both.
            progress?.Invoke("Stamping data format...");
            if (!await StampSourceDataFormatAsync(result))
            {
                return result;
            }

            // 1. Compare Database
            progress?.Invoke("Comparing database...");
            await CompareDatabase(result);

            // 2. Read current version
            progress?.Invoke("Reading version info...");
            await ReadVersionInfo(result);

            // 3. Compare map configs
            progress?.Invoke("Comparing map configs...");
            await CompareMapConfigs(result);

            // 4. Compare map SVGs
            progress?.Invoke("Comparing map SVG files...");
            await CompareMapSvgs(result);

            // 5. Compare marker icons
            progress?.Invoke("Comparing marker icons...");
            await CompareMarkerIcons(result);

            // 6. Compare item icons
            progress?.Invoke("Comparing item icons...");
            await CompareItemIcons(result);

            // 7. Compare hideout icons
            progress?.Invoke("Comparing hideout icons...");
            await CompareHideoutIcons(result);

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
    /// Writes the live data format into the source database's own header, using SQLite's
    /// user_version: the 32-bit slot SQLite reserves for the application and never reads
    /// itself. A published database then carries its contract with it, so a client can
    /// check what it downloaded against what it can read without having to trust the
    /// manifest that came alongside.
    /// </summary>
    private async Task<bool> StampSourceDataFormatAsync(ComparisonResult result)
    {
        var sourceDbPath = Path.Combine(_sourceBasePath, DatabaseFileName);
        // A missing source is reported by CompareDatabase as "not found"; that is a
        // clearer message than anything this step could produce.
        if (!File.Exists(sourceDbPath)) return true;

        try
        {
            await using (var connection = new SqliteConnection($"Data Source={sourceDbPath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                // PRAGMA takes no parameters; the value is an int this tool derived from
                // the repository layout, never user input.
                command.CommandText =
                    $"PRAGMA user_version = {result.LiveDataFormat.ToString(CultureInfo.InvariantCulture)}";
                await command.ExecuteNonQueryAsync();
            }

            return true;
        }
        catch (SqliteException ex)
        {
            // The file is there but SQLite will not open it. Publishing it anyway would
            // ship whatever it actually is to every install, so this stops here rather
            // than treating the stamp as optional.
            result.Success = false;
            result.ErrorMessage =
                $"{sourceDbPath} is not a database SQLite can open ({ex.Message}).\n\n" +
                "Rebuild the database in TarkovDBEditor before publishing.";
            return false;
        }
        finally
        {
            // Pooled connections keep the file open, and the publish copies it next.
            SqliteConnection.ClearAllPools();
        }
    }

    private async Task CompareDatabase(ComparisonResult result)
    {
        var sourceDbPath = Path.Combine(_sourceBasePath, DatabaseFileName);
        var targetDbPath = Path.Combine(result.ChannelDirPath!, DatabaseFileName);

        result.DbExists = File.Exists(sourceDbPath);

        if (!result.DbExists)
        {
            // Nothing to publish and nothing to repair a mirror from, so MirrorInSync is
            // left at its default rather than reporting a drift this run cannot fix.
            return;
        }

        result.SourceDbHash = await ComputeFileHashAsync(sourceDbPath);
        result.SourceDbSize = new FileInfo(sourceDbPath).Length;

        if (File.Exists(targetDbPath))
        {
            result.TargetDbHash = await ComputeFileHashAsync(targetDbPath);
            result.TargetDbSize = new FileInfo(targetDbPath).Length;
            result.DbChanged = result.SourceDbHash != result.TargetDbHash;
        }
        else
        {
            result.DbChanged = true; // New file
        }

        if (!result.MirrorsToAssets) return;

        // Both format-1 endpoints must serve the same bytes; a mismatch here means an
        // earlier publish reached only one of them.
        var mirrorDbPath = Path.Combine(_targetBasePath, DatabaseFileName);
        result.MirrorInSync = File.Exists(mirrorDbPath)
                              && File.Exists(targetDbPath)
                              && await ComputeFileHashAsync(mirrorDbPath) == result.TargetDbHash;
    }

    private async Task ReadVersionInfo(ComparisonResult result)
    {
        var versionPath = Path.Combine(result.ChannelDirPath!, VersionFileName);

        if (File.Exists(versionPath))
        {
            // First non-blank line only: later lines are endpoint directives (e.g.
            // "frozen"), not part of the version token.
            var lines = await File.ReadAllLinesAsync(versionPath);
            result.CurrentVersion = lines
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.Length > 0) ?? "0.0.0";
        }
        else
        {
            result.CurrentVersion = "0.0.0";
        }

        // The stamps are half of what the endpoints serve, so they count toward mirror
        // sync too: a version-only drift would otherwise leave the tool with nothing to
        // publish while the two format-1 endpoints answered differently.
        if (result.MirrorsToAssets)
        {
            var mirrorVersionPath = Path.Combine(_targetBasePath, VersionFileName);
            var stampsMatch = File.Exists(mirrorVersionPath)
                              && File.Exists(versionPath)
                              && await ComputeFileHashAsync(mirrorVersionPath)
                                 == await ComputeFileHashAsync(versionPath);
            result.MirrorInSync = result.MirrorInSync && stampsMatch;
        }

        // Suggest new version (increment patch)
        if (Version.TryParse(result.CurrentVersion, out var currentVer))
        {
            result.NewVersion = $"{currentVer.Major}.{currentVer.Minor}.{currentVer.Build + 1}";
        }
        else
        {
            result.NewVersion = "1.0.0";
        }
    }

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

    private async Task CompareMapSvgs(ComparisonResult result)
    {
        var sourceDir = Path.Combine(_sourceBasePath, "Resources", "Maps");
        var targetDir = Path.Combine(_targetBasePath, "DB", "Maps");

        await CompareFiles(sourceDir, targetDir, "*.svg",
            result.MapSvgChanges,
            added => result.MapSvgAdded = added,
            updated => result.MapSvgUpdated = updated,
            unchanged => result.MapSvgUnchanged = unchanged);
    }

    private async Task CompareMarkerIcons(ComparisonResult result)
    {
        var sourceDir = Path.Combine(_sourceBasePath, "Resources", "Icons");
        var targetDir = Path.Combine(_targetBasePath, "DB", "Icons");

        await CompareFiles(sourceDir, targetDir, "*.webp",
            result.MarkerIconChanges,
            added => result.MarkerIconAdded = added,
            updated => result.MarkerIconUpdated = updated,
            unchanged => result.MarkerIconUnchanged = unchanged);
    }

    private async Task CompareItemIcons(ComparisonResult result)
    {
        var sourceDir = Path.Combine(_sourceBasePath, "wiki_data", "icons");
        var targetDir = Path.Combine(_targetBasePath, "icons");

        await CompareFiles(sourceDir, targetDir, "*.png",
            result.ItemIconChanges,
            added => result.ItemIconAdded = added,
            updated => result.ItemIconUpdated = updated,
            unchanged => result.ItemIconUnchanged = unchanged);
    }

    private async Task CompareHideoutIcons(ComparisonResult result)
    {
        var sourceDir = Path.Combine(_sourceBasePath, "icons", "hideout");
        var targetDir = Path.Combine(_targetBasePath, "icons", "hideout");

        await CompareFiles(sourceDir, targetDir, "*.png",
            result.HideoutIconChanges,
            added => result.HideoutIconAdded = added,
            updated => result.HideoutIconUpdated = updated,
            unchanged => result.HideoutIconUnchanged = unchanged);
    }

    private async Task CompareFiles(string sourceDir, string targetDir, string pattern,
        List<FileChangeInfo> changes,
        Action<int> setAdded, Action<int> setUpdated, Action<int> setUnchanged)
    {
        int added = 0, updated = 0, unchanged = 0;

        if (!Directory.Exists(sourceDir))
        {
            setAdded(0);
            setUpdated(0);
            setUnchanged(0);
            return;
        }

        var sourceFiles = Directory.GetFiles(sourceDir, pattern, SearchOption.TopDirectoryOnly);

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
                results.Add(info);
            }
            else
            {
                var targetSize = new FileInfo(targetFile).Length;
                info.TargetSize = targetSize;

                // Quick check: if file sizes differ, they're definitely different
                if (sourceSize != targetSize)
                {
                    info.Type = ChangeType.Updated;
                    results.Add(info);
                }
                else
                {
                    // Same size - need to compare content via hash
                    var sourceHash = await ComputeFileHashAsync(sourceFile);
                    var targetHash = await ComputeFileHashAsync(targetFile);

                    if (sourceHash != targetHash)
                    {
                        info.Type = ChangeType.Updated;
                        results.Add(info);
                    }
                    else
                    {
                        info.Type = ChangeType.Unchanged;
                        results.Add(info);
                    }
                }
            }
        });

        // Count results
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

        setAdded(added);
        setUpdated(updated);
        setUnchanged(unchanged);
    }

    /// <summary>
    /// Publish all changed files to TarkovHelper Assets folder
    /// </summary>
    public async Task<PublishResult> PublishAsync(ComparisonResult comparison, string newVersion, Action<string>? progress = null)
    {
        var result = new PublishResult { Success = true, NewVersion = newVersion };

        try
        {
            if (comparison.ChannelDirPath == null)
            {
                result.Success = false;
                result.ErrorMessage = "Comparison did not resolve a data channel; re-run the comparison.";
                return result;
            }

            // 1. Copy database to the channel endpoint, and to the Assets mirror while
            //    format 1 is live (using stream to handle files open by other processes).
            //    Copied even when unchanged if the mirror has drifted, so one publish
            //    always leaves both endpoints byte-identical.
            //    DbExists is part of the condition because a mirror can also fall out of
            //    sync on the version stamp alone, and a repair must never reach for a
            //    source database this build output does not have.
            if (comparison.DbExists && (comparison.DbChanged || comparison.MirrorNeedsRepair))
            {
                progress?.Invoke("Copying database...");
                var sourceDbPath = Path.Combine(_sourceBasePath, DatabaseFileName);
                var channelDbPath = Path.Combine(comparison.ChannelDirPath, DatabaseFileName);

                Directory.CreateDirectory(comparison.ChannelDirPath);
                await CopyFileWithShareAsync(sourceDbPath, channelDbPath);
                result.CopiedFiles.Add($"{DataChannelDirName}/v{comparison.LiveDataFormat}/{DatabaseFileName}");
                result.FilesCopied++;

                if (comparison.MirrorsToAssets)
                {
                    await CopyFileWithShareAsync(sourceDbPath, Path.Combine(_targetBasePath, DatabaseFileName));
                    result.CopiedFiles.Add(DatabaseFileName);
                    result.FilesCopied++;
                }
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

            // 3. Copy map SVGs
            progress?.Invoke("Copying map SVGs...");
            var svgTargetDir = Path.Combine(_targetBasePath, "DB", "Maps");
            Directory.CreateDirectory(svgTargetDir);
            foreach (var change in comparison.MapSvgChanges.Where(c => c.Type != ChangeType.Unchanged))
            {
                File.Copy(change.SourcePath, change.TargetPath, overwrite: true);
                result.CopiedFiles.Add($"DB/Maps/{change.FileName}");
                result.FilesCopied++;
            }

            // 4. Copy marker icons
            progress?.Invoke("Copying marker icons...");
            var markerTargetDir = Path.Combine(_targetBasePath, "DB", "Icons");
            Directory.CreateDirectory(markerTargetDir);
            foreach (var change in comparison.MarkerIconChanges.Where(c => c.Type != ChangeType.Unchanged))
            {
                File.Copy(change.SourcePath, change.TargetPath, overwrite: true);
                result.IconsCopied++;
            }

            // 5. Copy item icons
            progress?.Invoke($"Copying item icons ({comparison.ItemIconChanges.Count} changes)...");
            var itemIconTargetDir = Path.Combine(_targetBasePath, "icons");
            Directory.CreateDirectory(itemIconTargetDir);
            int iconCount = 0;
            foreach (var change in comparison.ItemIconChanges.Where(c => c.Type != ChangeType.Unchanged))
            {
                File.Copy(change.SourcePath, change.TargetPath, overwrite: true);
                result.IconsCopied++;
                iconCount++;

                if (iconCount % 100 == 0)
                {
                    progress?.Invoke($"Copying item icons ({iconCount}/{comparison.ItemIconChanges.Count})...");
                }
            }

            // 6. Copy hideout icons
            progress?.Invoke("Copying hideout icons...");
            var hideoutTargetDir = Path.Combine(_targetBasePath, "icons", "hideout");
            Directory.CreateDirectory(hideoutTargetDir);
            foreach (var change in comparison.HideoutIconChanges.Where(c => c.Type != ChangeType.Unchanged))
            {
                File.Copy(change.SourcePath, change.TargetPath, overwrite: true);
                result.IconsCopied++;
            }

            // 7. Write the manifest and the version stamps last, after the database they
            //    describe: an interrupted publish then leaves the old version pointing at
            //    data that is merely newer, never a new version (or hash) pointing at
            //    data that never arrived.
            progress?.Invoke("Updating manifest...");
            await WriteManifestAsync(comparison, newVersion, result);

            await File.WriteAllTextAsync(
                Path.Combine(comparison.ChannelDirPath, VersionFileName), newVersion);
            result.CopiedFiles.Add($"{DataChannelDirName}/v{comparison.LiveDataFormat}/{VersionFileName}");

            if (comparison.MirrorsToAssets)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(_targetBasePath, VersionFileName), newVersion);
                result.CopiedFiles.Add(VersionFileName);
            }

            // 8. The channel index names the schema currently published. Rewritten every
            //    time so it cannot drift, and it is the only mutable part of the channel:
            //    superseded endpoint directories are never touched again, which is how a
            //    build learns it was left behind without anyone hand-editing history.
            await WriteIndexAsync(comparison.LiveDataFormat, result);

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
    /// Writes data/v&lt;N&gt;/manifest.json: the document new builds read. The hash and size
    /// travel in the same document as the version so a client cannot pair a fresh
    /// version with a stale or truncated database, which per-file CDN caching otherwise
    /// allows.
    /// </summary>
    private async Task WriteManifestAsync(ComparisonResult comparison, string newVersion, PublishResult result)
    {
        var databasePath = Path.Combine(comparison.ChannelDirPath!, DatabaseFileName);

        var manifest = new
        {
            schemaVersion = ManifestSchemaVersion,
            dataFormat = comparison.LiveDataFormat,
            version = newVersion,
            database = new
            {
                file = DatabaseFileName,
                sha256 = await ComputeFileSha256Async(databasePath),
                size = new FileInfo(databasePath).Length,
            },
        };

        var path = Path.Combine(comparison.ChannelDirPath!, ManifestFileName);
        await File.WriteAllTextAsync(path, ToJson(manifest));
        result.CopiedFiles.Add($"{DataChannelDirName}/v{comparison.LiveDataFormat}/{ManifestFileName}");
        result.FilesCopied++;
    }

    /// <summary>
    /// Writes data/index.json, which names the data format the project publishes right
    /// now. Builds pinned to an older format compare against it to learn that nothing
    /// further is coming for them.
    /// </summary>
    private async Task WriteIndexAsync(int liveDataFormat, PublishResult result)
    {
        var index = new { schemaVersion = IndexSchemaVersion, currentDataFormat = liveDataFormat };

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
