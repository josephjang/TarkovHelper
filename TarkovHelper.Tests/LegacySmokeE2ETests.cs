using System.IO;
using System.Net.Http;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// R9 and R4 on the build already installed in the field: the release users are running today
/// must keep working against the refreshed data, and progress it recorded before the refresh
/// must still show afterwards.
/// <para>
/// This is the only check that covers those users. Every install in the field runs v2026.7.0,
/// which predates the data channel: it polls for a database every five minutes, installs
/// whatever it downloads with no verification, and cannot be fixed after the fact. So the
/// candidate database has to be proven readable by that build before it is published, not
/// after.
/// </para>
/// <para>
/// What that build honours, spelled out because assuming wrongly here is exactly how this suite
/// would pass without ever reading the candidate:
/// </para>
/// <list type="bullet">
/// <item><description>
/// TARKOVHELPER_CONFIG_PATH, and no other harness variable. In particular NOT
/// TARKOVHELPER_DISABLE_DB_UPDATE: the commit that taught DatabaseUpdateService to read that
/// variable landed after v2026.7.0 was tagged, so the harness setting it does nothing here.
/// </description></item>
/// <item><description>
/// Its only data-update gate is <c>LocalVersion == remoteVersion</c>, comparing the
/// Assets/db_version.txt in its own folder against the copy raw main serves. Every publish
/// rewrites that token, so the one baked into an extracted release goes stale the first time
/// the data is republished, and the fielded build would then download the LIVE database over
/// the staged candidate at startup, before it loads a single page. So
/// <see cref="PrepareLegacyBuild"/> pins the local token to whatever that URL serves right
/// now, and every test calls <see cref="LegacyBuild.AssertItReadTheCandidate"/> afterwards to
/// prove the file on disk is still the candidate, byte for byte. A run that cannot establish
/// the pin fails loudly rather than proving nothing quietly.
/// </description></item>
/// <item><description>
/// Its app-update check needs no neutralising: a newer update.xml only flips a header button
/// (MainWindow.OnUpdateCheckCompleted), and AutoUpdater runs only when that button is clicked,
/// so nothing this suite drives is affected.
/// </description></item>
/// <item><description>
/// Its quest tab has no status chips, so the smoke waits on the list and reads the detail
/// status text rather than driving the chip row.
/// </description></item>
/// </list>
/// <para>
/// Requires an extracted previous-release folder and a candidate database, both named by
/// environment variables; the suite skips when either is absent, because neither exists on a
/// machine that has not cut a release.
/// </para>
/// </summary>
[Collection("E2E")]
[Trait("Category", "E2E")]
public sealed class LegacySmokeE2ETests : E2ETestBase
{
    /// <summary>Folder holding an extracted previous-release zip (the one with TarkovHelper.dll in it).</summary>
    public const string LegacyAppDirVariable = "TARKOVHELPER_LEGACY_APP_DIR";

    /// <summary>The regenerated database the publish is about to ship.</summary>
    public const string CandidateDbVariable = "TARKOVHELPER_CANDIDATE_DB";

    /// <summary>
    /// The URL v2026.7.0's DatabaseUpdateService reads its "is my data current?" token from,
    /// copied out of that build rather than referenced from today's source: the current build
    /// reads a versioned data channel the fielded one knows nothing about. A tag's source cannot
    /// change, so this literal cannot drift from the build it describes.
    /// </summary>
    private const string LegacyVersionUrl =
        "https://raw.githubusercontent.com/josephjang/Tarkov-Item-Helper/refs/heads/main/TarkovHelper/Assets/db_version.txt";

    [LegacySmokeFact]
    public void The_fielded_build_loads_every_page_against_the_candidate_data()
    {
        var build = PrepareLegacyBuild();
        E2EDb.CreateUserDataDb(build.ConfigDir);
        E2EDb.SeedSetting(build.ConfigDir, "app.logMonitoringEnabled", "False");

        using (var app = LaunchLegacy(build))
        {
            // Each tab's list control is the page's own "I loaded" signal; a page that threw would
            // never publish one.
            app.SelectTab("TabQuests", "LstQuests");
            AppDriver.PollUntil(() => app.GetListItemCount("LstQuests") > 0, "the quest list to fill");

            app.SelectTab("TabHideout", "LstHideout");
            app.SelectTab("TabItems", "LstItems");
            app.SelectTab("TabCollector", "LstCollectorQuests");
        }

        build.AssertItReadTheCandidate();
    }

    [LegacySmokeFact]
    public void A_completion_recorded_before_the_refresh_shows_against_the_renamed_quest()
    {
        var build = PrepareLegacyBuild();
        var renamed = FindCarriedRename(CandidateDatabase());

        Assert.True(renamed is not null,
            "the candidate holds no quest whose stored NormalizedName differs from its title and " +
            "whose title is a unique search substring, so there is no carried rename to drive. " +
            "A candidate produced by the 1.1 refresh has 91 of them; one without any is either " +
            "missing the Quests.NormalizedName column or was not built by the identity pipeline.");

        // Seeded the way v2026.7.0 writes progress: the row key it knew, and the normalized name
        // it computed from the title it knew.
        //
        // The profile column is filled with "pvp" even though that build predates profiles
        // entirely: its QuestProgress reads carry no profile filter, and the column the current
        // schema creates defaults to exactly this value, so a row written here is the row that
        // build reads. Log monitoring is off so a real EFT log on the machine cannot sync a
        // quest event over the seeded state mid-test.
        E2EDb.CreateUserDataDb(build.ConfigDir);
        E2EDb.SeedSetting(build.ConfigDir, "app.logMonitoringEnabled", "False");
        E2EDb.SeedQuestProgress(
            build.ConfigDir, ProfileService.PvpProfileId, renamed!.Id, renamed.NormalizedName, "Done");

        using (var app = LaunchLegacy(build))
        {
            app.SelectTab("TabQuests", "LstQuests");
            app.SetTextBoxValue("TxtSearch", renamed.Name);
            AppDriver.PollUntil(() => app.GetListItemCount("LstQuests") == 1,
                $"quest list to filter down to '{renamed.Name}'");
            app.SelectListItemAt("LstQuests", 0);

            AppDriver.PollUntil(() => app.GetElementText("TxtDetailName") == renamed.Name,
                $"detail panel to show '{renamed.Name}'");
            AppDriver.PollUntil(() => app.GetElementText("TxtDetailStatus").Contains("Done", StringComparison.OrdinalIgnoreCase)
                    || app.GetElementText("TxtDetailStatus").Contains("완료", StringComparison.Ordinal),
                $"'{renamed.Name}' to show as done in the fielded build");
        }

        build.AssertItReadTheCandidate();
    }

    #region Scratch build

    /// <summary>
    /// A scratch copy of the fielded release, staged with the candidate database, plus the two
    /// facts a run needs afterwards to prove the build actually read that candidate: the
    /// candidate's hash, and the version token the data check was pinned to.
    /// </summary>
    private sealed record LegacyBuild(
        string Scratch, string ConfigDir, string CandidateHash, string? PinnedVersionToken)
    {
        public string AssetsDir => Path.Combine(Scratch, "Assets");

        public string DatabasePath => Path.Combine(AssetsDir, "tarkov_data.db");

        /// <summary>
        /// Fails unless the database the build spent the run reading is still the candidate.
        /// The evidence it reads, and why that evidence is the whole proof, is documented on
        /// <see cref="StagedDatabase"/>, which is also where the failure paths are unit tested.
        /// </summary>
        public void AssertItReadTheCandidate()
            => StagedDatabase.AssertStillStaged(DatabasePath, CandidateHash, PinnedVersionToken);
    }

    /// <summary>
    /// Copies the release into a scratch folder, puts the candidate database where the app reads
    /// it, and pins the version token so the build's undisableable five-minute data check reports
    /// up to date instead of downloading the published database over the candidate.
    /// </summary>
    private LegacyBuild PrepareLegacyBuild()
    {
        var source = Environment.GetEnvironmentVariable(LegacyAppDirVariable)!;
        var scratch = Path.Combine(NewConfigDir(), "legacy-app");
        CopyDirectory(source, scratch);

        var assets = Path.Combine(scratch, "Assets");
        Directory.CreateDirectory(assets);

        var candidate = CandidateDatabase();
        var candidateHash = StagedDatabase.Sha256(candidate);
        var staged = Path.Combine(assets, "tarkov_data.db");
        File.Copy(candidate, staged, overwrite: true);
        Assert.True(StagedDatabase.Sha256(staged) == candidateHash,
            $"copying {candidate} into {staged} did not reproduce it byte for byte");

        var versionFile = Path.Combine(assets, StagedDatabase.VersionFileName);
        var liveToken = LiveVersionToken();
        if (liveToken != null)
            File.WriteAllText(versionFile, liveToken);
        var pinnedToken = File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : null;

        var configDir = Path.Combine(scratch, "SmokeConfig");
        Directory.CreateDirectory(configDir);

        return new LegacyBuild(scratch, configDir, candidateHash, pinnedToken);
    }

    /// <summary>
    /// The database version token the fielded build's check will read, or null when that build
    /// would get no token either.
    /// <para>
    /// Its GetRemoteVersionAsync treats every HTTP failure as "no remote version" and returns
    /// without downloading, so a non-success status here means the candidate is safe and there is
    /// nothing to pin. A transport failure is different: this host failing to reach GitHub for one
    /// request does not mean the app will fail too, and the app retries every five minutes for the
    /// whole run. That case throws, because a smoke that cannot pin the check cannot know which
    /// database it exercised.
    /// </para>
    /// </summary>
    private static string? LiveVersionToken()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TarkovHelper/1.0");

        HttpResponseMessage response;
        try
        {
            response = http.GetAsync(LegacyVersionUrl).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not reach {LegacyVersionUrl} ({ex.Message}). Without the live token the fielded " +
                "build's five-minute data check cannot be pinned, and it would download the published " +
                "database over the candidate, leaving this smoke green having never read the candidate. " +
                "Re-run once the token is reachable.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                return null;

            var token = response.Content.ReadAsStringAsync().GetAwaiter().GetResult().Trim();

            // The fielded build's own check also treats an empty body as "no remote version".
            return token.Length == 0 ? null : token;
        }
    }

    private static AppDriver LaunchLegacy(LegacyBuild build)
    {
        var dll = Path.Combine(build.Scratch, "TarkovHelper.dll");
        Assert.True(File.Exists(dll), $"{dll} is missing; point {LegacyAppDirVariable} at an extracted release");

        var app = AppDriver.Launch(dll, build.ConfigDir);
        try
        {
            app.ShowWindow(Win32.SW_MAXIMIZE);
            return app;
        }
        catch
        {
            app.Dispose();
            throw;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);

        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static string CandidateDatabase() => Environment.GetEnvironmentVariable(CandidateDbVariable)!;

    #endregion

    #region Reading the candidate

    /// <summary>
    /// Finds a quest in the candidate database whose stored normalized name no longer matches
    /// its title: that is a rename whose row key and progress key were carried across. The name
    /// must be a unique search substring so the quest list filters to one row, which is what
    /// <see cref="E2EQuests"/> selects for.
    /// </summary>
    private static E2EQuests.Quest? FindCarriedRename(string databasePath)
        => E2EQuests.Read(databasePath).UniquelySearchable.FirstOrDefault(q => q.IsCarriedRename);

    #endregion
}

/// <summary>
/// Skips the legacy smoke unless both an extracted previous release and a candidate database
/// are named. Neither exists on a machine that has not cut a release, and the whole point of
/// the suite is to run against real artefacts rather than a fixture.
/// </summary>
public sealed class LegacySmokeFactAttribute : FactAttribute
{
    public LegacySmokeFactAttribute()
    {
        var appDir = Environment.GetEnvironmentVariable(LegacySmokeE2ETests.LegacyAppDirVariable);
        var candidate = Environment.GetEnvironmentVariable(LegacySmokeE2ETests.CandidateDbVariable);

        if (string.IsNullOrEmpty(appDir) || !Directory.Exists(appDir))
        {
            Skip = $"Set {LegacySmokeE2ETests.LegacyAppDirVariable} to an extracted previous-release folder.";
        }
        else if (string.IsNullOrEmpty(candidate) || !File.Exists(candidate))
        {
            Skip = $"Set {LegacySmokeE2ETests.CandidateDbVariable} to the candidate tarkov_data.db.";
        }
    }
}
