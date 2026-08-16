using System.IO;
using Microsoft.Data.Sqlite;
using TarkovHelper.Models;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// Real-SQLite guards for <c>UserDataDbService.ResetProfileAsync</c> and the RaidHistory
/// ownership migration (feature-complete-profile-reset.spec.md), built through the internal
/// path-taking constructor against a temp file per test. The transactional behavior is the
/// point: the fake cannot prove all-or-nothing, rollback, or schema migration, so these run
/// against the real store.
/// </summary>
[Collection(SchedulingSensitiveCollection.Name)]
public sealed class ProfileResetStoreTests : IDisposable
{
    private const string Season = "season";
    private const string Pve = "pve";

    private readonly TempStoreRoot _stores = new("reset");

    public void Dispose() => _stores.Dispose();

    private UserDataDbService NewStore() => _stores.NewStore();

    /// <summary>
    /// A database path with no store built around it yet, for the cases that have to write the
    /// file themselves (a pre-upgrade schema) or hand it to more than one service at once.
    /// </summary>
    private string NewStorePath()
        => Path.Combine(_stores.Root, Guid.NewGuid().ToString("N") + ".db");

    /// <summary>Seeds one profile's rows across every table a reset touches.</summary>
    private static async Task SeedProfileAsync(UserDataDbService store, string profileId)
    {
        await store.SaveQuestProgressAsync($"q-{profileId}", $"quest-{profileId}", QuestStatus.Done, profileId);
        await store.SaveObjectiveProgressAsync($"quest-{profileId}:0", $"q-{profileId}", true, profileId);
        await store.SaveHideoutProgressAsync("workbench", 2, profileId);
        await store.SaveItemInventoryAsync("salewa", 1, 2, profileId);
        await store.SetProfileSettingAsync(profileId, "app.playerLevel", "42");
        await store.SetProfileSettingAsync(profileId, "app.hasEodEdition", "True");
        await store.SaveRaidHistoryAsync(new EftRaidInfo
        {
            RaidId = $"raid-{profileId}",
            AppProfileId = profileId,
            MapKey = "Customs",
            StartTime = new DateTime(2026, 8, 10, 20, 0, 0),
        });
    }

    [Fact]
    public async Task A_reset_clears_only_the_target_profile_and_leaves_the_other_intact()
    {
        var store = NewStore();
        await SeedProfileAsync(store, Season);
        await SeedProfileAsync(store, Pve);
        await store.SetSettingAsync("app.language", "EN");

        await store.ResetProfileAsync(
            Season, new DateTime(2026, 8, 13, 12, 0, 0), SettingsService.ProfileKeysSurvivingReset);

        // The target owns nothing any more (PRD R3)...
        Assert.Empty(await store.LoadQuestProgressAsync(Season));
        Assert.Empty(await store.LoadObjectiveProgressAsync(Season));
        Assert.Empty(await store.LoadHideoutProgressAsync(Season));
        Assert.Empty(await store.LoadItemInventoryAsync(Season));
        Assert.Null(await store.GetProfileSettingAsync(Season, "app.playerLevel"));

        // ...while the other profile's rows are untouched, value for value (PRD R4).
        Assert.Equal(QuestStatus.Done, (await store.LoadQuestProgressAsync(Pve))[$"quest-{Pve}"]);
        Assert.True((await store.LoadObjectiveProgressAsync(Pve))[$"quest-{Pve}:0"]);
        Assert.Equal(2, (await store.LoadHideoutProgressAsync(Pve))["workbench"]);
        Assert.Equal((1, 2), (await store.LoadItemInventoryAsync(Pve))["salewa"]);
        Assert.Equal("42", await store.GetProfileSettingAsync(Pve, "app.playerLevel"));

        // Account-wide settings survive too.
        Assert.Equal("EN", await store.GetSettingAsync("app.language"));
    }

    [Fact]
    public async Task Editions_and_the_watermark_survive_and_every_other_profile_setting_is_wiped()
    {
        var store = NewStore();
        await store.SetProfileSettingAsync(Season, "app.playerLevel", "42");
        await store.SetProfileSettingAsync(Season, "app.scavRep", "3.5");
        await store.SetProfileSettingAsync(Season, "app.playerFaction", "bear");
        await store.SetProfileSettingAsync(Season, "app.prestigeLevel", "2");
        await store.SetProfileSettingAsync(Season, "app.hasEodEdition", "True");
        await store.SetProfileSettingAsync(Season, "app.hasUnheardEdition", "True");

        var resetAt = new DateTime(2026, 8, 13, 12, 0, 0);
        await store.ResetProfileAsync(Season, resetAt, SettingsService.ProfileKeysSurvivingReset);

        // The editions describe what the account owns; a reset never asks the player to
        // restate a fact it could not have changed (PRD R4).
        Assert.Equal("True", await store.GetProfileSettingAsync(Season, "app.hasEodEdition"));
        Assert.Equal("True", await store.GetProfileSettingAsync(Season, "app.hasUnheardEdition"));

        // Everything progress-shaped is back to defaults-by-absence (PRD R3).
        Assert.Null(await store.GetProfileSettingAsync(Season, "app.playerLevel"));
        Assert.Null(await store.GetProfileSettingAsync(Season, "app.scavRep"));
        Assert.Null(await store.GetProfileSettingAsync(Season, "app.playerFaction"));
        Assert.Null(await store.GetProfileSettingAsync(Season, "app.prestigeLevel"));

        // And the fence is up, in the same commit.
        Assert.Equal(resetAt, await store.GetProgressResetAtAsync(Season));
    }

    /// <summary>
    /// The empty-survivor case takes the other branch of the settings delete, the one without a
    /// NOT IN clause, and it is the branch that could sweep the watermark it just wrote. Every
    /// other test here passes the non-empty <c>ProfileKeysSurvivingReset</c>, so nothing else
    /// exercises it. Preserving nothing must still mean "reset happened", not "no fence".
    /// </summary>
    [Fact]
    public async Task A_reset_preserving_nothing_wipes_every_setting_and_still_leaves_its_watermark()
    {
        var store = NewStore();
        await SeedProfileAsync(store, Season);
        await SeedProfileAsync(store, Pve);
        await store.SetProfileSettingAsync(Season, "app.hasUnheardEdition", "True");

        var resetAt = new DateTime(2026, 8, 13, 12, 0, 0);
        await store.ResetProfileAsync(Season, resetAt, Array.Empty<string>());

        // Nothing is named as a survivor, so even the editions go.
        Assert.Null(await store.GetProfileSettingAsync(Season, "app.playerLevel"));
        Assert.Null(await store.GetProfileSettingAsync(Season, "app.hasEodEdition"));
        Assert.Null(await store.GetProfileSettingAsync(Season, "app.hasUnheardEdition"));

        // The watermark is written after that delete, so it survives it.
        Assert.Equal(resetAt, await store.GetProgressResetAtAsync(Season));

        // And the unrelated profile keeps its settings: an empty survivor list widens what is
        // deleted within the profile, never across profiles.
        Assert.Equal("42", await store.GetProfileSettingAsync(Pve, "app.playerLevel"));
        Assert.Equal("True", await store.GetProfileSettingAsync(Pve, "app.hasEodEdition"));
        Assert.Null(await store.GetProgressResetAtAsync(Pve));
    }

    [Fact]
    public async Task A_failure_before_the_commit_rolls_back_everything_including_the_watermark()
    {
        var store = NewStore();
        await SeedProfileAsync(store, Season);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ResetProfileAsync(
            Season, new DateTime(2026, 8, 13, 12, 0, 0), SettingsService.ProfileKeysSurvivingReset,
            beforeCommit: () => throw new InvalidOperationException("disk full")));

        // The caller sees the failure that stopped the reset, not the rollback that followed it:
        // ProfileResetService puts this message in front of the player.
        Assert.Equal("disk full", thrown.Message);

        // Nothing was removed (PRD R5): every table still holds the seeded rows, and no fence
        // went up for data that is still there.
        Assert.Equal(QuestStatus.Done, (await store.LoadQuestProgressAsync(Season))[$"quest-{Season}"]);
        Assert.True((await store.LoadObjectiveProgressAsync(Season))[$"quest-{Season}:0"]);
        Assert.Equal(2, (await store.LoadHideoutProgressAsync(Season))["workbench"]);
        Assert.Equal((1, 2), (await store.LoadItemInventoryAsync(Season))["salewa"]);
        Assert.Equal("42", await store.GetProfileSettingAsync(Season, "app.playerLevel"));
        Assert.Single(await store.GetRaidHistoryAsync());
        Assert.Null(await store.GetProgressResetAtAsync(Season));
    }

    [Fact]
    public async Task A_reset_deletes_only_raids_owned_by_the_target_profile()
    {
        var store = NewStore();
        await store.SaveRaidHistoryAsync(new EftRaidInfo
        {
            RaidId = "owned-by-season", AppProfileId = Season,
            StartTime = new DateTime(2026, 8, 10, 20, 0, 0),
        });
        await store.SaveRaidHistoryAsync(new EftRaidInfo
        {
            RaidId = "owned-by-pve", AppProfileId = Pve,
            StartTime = new DateTime(2026, 8, 10, 21, 0, 0),
        });
        // A legacy row: no owner, and never deleted by any profile reset (PRD R9).
        await store.SaveRaidHistoryAsync(new EftRaidInfo
        {
            RaidId = "legacy-unowned", AppProfileId = null,
            StartTime = new DateTime(2026, 8, 10, 22, 0, 0),
        });

        await store.ResetProfileAsync(
            Season, new DateTime(2026, 8, 13, 12, 0, 0), SettingsService.ProfileKeysSurvivingReset);

        var remaining = await store.GetRaidHistoryAsync();
        Assert.Equal(2, remaining.Count);
        Assert.DoesNotContain(remaining, r => r.RaidId == "owned-by-season");
        Assert.Equal(Pve, remaining.Single(r => r.RaidId == "owned-by-pve").AppProfileId);
        Assert.Null(remaining.Single(r => r.RaidId == "legacy-unowned").AppProfileId);
    }

    [Fact]
    public async Task A_fresh_database_round_trips_the_raid_owner_column()
    {
        var store = NewStore();

        await store.SaveRaidHistoryAsync(new EftRaidInfo
        {
            RaidId = "r-1", AppProfileId = Season,
            StartTime = new DateTime(2026, 8, 10, 20, 0, 0),
        });

        Assert.Equal(Season, (await store.GetRaidHistoryAsync()).Single().AppProfileId);
    }

    /// <summary>
    /// Writes the RaidHistory schema as it was before raid ownership existed, with one row in
    /// it, so the upgrade migration has something real to migrate.
    /// </summary>
    private static async Task CreatePreUpgradeDatabaseAsync(string dbPath)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE RaidHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RaidId TEXT, SessionId TEXT, ShortId TEXT, ProfileId TEXT,
                RaidType INTEGER NOT NULL DEFAULT 0, GameMode INTEGER NOT NULL DEFAULT 0,
                MapName TEXT, MapKey TEXT, ServerIp TEXT, ServerPort INTEGER,
                IsParty INTEGER NOT NULL DEFAULT 0, PartyLeaderAccountId TEXT,
                StartTime TEXT, EndTime TEXT, DurationSeconds INTEGER,
                Rtt REAL, PacketLoss REAL, PacketsSent INTEGER, PacketsReceived INTEGER,
                CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            INSERT INTO RaidHistory (RaidId, StartTime) VALUES ('pre-upgrade', '2026-08-01T20:00:00');";
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task A_pre_upgrade_database_gains_the_owner_column_with_existing_rows_as_legacy()
    {
        var dbPath = NewStorePath();
        await CreatePreUpgradeDatabaseAsync(dbPath);

        var store = new UserDataDbService(dbPath);
        await store.InitializeAsync();

        // The old row is preserved and reads back as legacy (null owner)...
        var raid = (await store.GetRaidHistoryAsync()).Single();
        Assert.Equal("pre-upgrade", raid.RaidId);
        Assert.Null(raid.AppProfileId);

        // ...and running the migration again on another instance is a no-op, not an error.
        var again = new UserDataDbService(dbPath);
        await again.InitializeAsync();
        Assert.Single(await again.GetRaidHistoryAsync());
    }

    /// <summary>
    /// The upgrade launch is the one launch where this migration runs, and roughly every method
    /// on the service starts by awaiting initialization from whatever thread it landed on. If
    /// two of them can both see the column as missing and both ALTER, the loser throws
    /// "duplicate column name" out of, say, LoadQuestProgressAsync, and the player's first sight
    /// of the new build is an empty quest list.
    /// </summary>
    [Fact]
    public async Task Concurrent_initialization_of_a_pre_upgrade_database_migrates_once_without_error()
    {
        var dbPath = NewStorePath();

        // A database from the previous release: every other table already current, only the raid
        // owner column missing. That is the shape that makes the race real - initialization has
        // no other write to do, so all of its callers reach the column check together.
        var previousRelease = new UserDataDbService(dbPath);
        await previousRelease.InitializeAsync();
        await previousRelease.SaveRaidHistoryAsync(new EftRaidInfo
        {
            RaidId = "pre-upgrade", StartTime = new DateTime(2026, 8, 1, 20, 0, 0),
        });
        await using (var editor = new SqliteConnection($"Data Source={dbPath}"))
        {
            await editor.OpenAsync();
            await using var drop = editor.CreateCommand();
            drop.CommandText = "ALTER TABLE RaidHistory DROP COLUMN AppProfileId";
            await drop.ExecuteNonQueryAsync();
        }

        const int racers = 8;
        var stores = Enumerable.Range(0, racers).Select(_ => new UserDataDbService(dbPath)).ToList();
        using var start = new Barrier(racers);

        // Separate instances over one file: the worst case the migration has to survive, since
        // an instance-level lock cannot order these. Whoever loses the ALTER must accept the
        // column someone else added rather than throw.
        var attempts = stores.Select(store => Task.Run(async () =>
        {
            start.SignalAndWait();
            await store.InitializeAsync();
        }));

        // Task.WhenAll surfaces the first failure; an AggregateException would fail here too.
        await Task.WhenAll(attempts);

        Assert.All(stores, store => Assert.True(store.IsInitialized));

        // The same instance under concurrent callers initializes once and answers all of them.
        var shared = new UserDataDbService(dbPath);
        using var startShared = new Barrier(racers);
        await Task.WhenAll(Enumerable.Range(0, racers).Select(_ => Task.Run(async () =>
        {
            startShared.SignalAndWait();
            await shared.InitializeAsync();
        })));
        Assert.True(shared.IsInitialized);

        // The column exists exactly once, and the pre-upgrade row survived as legacy.
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM pragma_table_info('RaidHistory') WHERE name='AppProfileId'";
        Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));

        var raid = (await shared.GetRaidHistoryAsync()).Single();
        Assert.Equal("pre-upgrade", raid.RaidId);
        Assert.Null(raid.AppProfileId);
    }

    /// <summary>
    /// A rollback can fail on its own (SQLite already rolled the transaction back, or the
    /// connection is gone), and an unguarded one in a <c>catch { ...; throw; }</c> swallows the
    /// failure it was reacting to. This pins the guard: the rollback that would throw does not.
    /// </summary>
    [Fact]
    public async Task A_rollback_that_cannot_run_is_swallowed_so_the_original_failure_survives()
    {
        var dbPath = NewStorePath();
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE T (X TEXT)";
        await command.ExecuteNonQueryAsync();

        // A transaction whose connection has gone away: exactly what a broken connection or an
        // auto-rollback leaves behind, and the raw call this guard replaced throws on it.
        await using var closed = (SqliteTransaction)await connection.BeginTransactionAsync();
        await connection.CloseAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => closed.RollbackAsync());

        await UserDataDbService.RollbackSafelyAsync(closed, "test");

        // An already completed transaction is the other shape of the same hazard.
        await connection.OpenAsync();
        await using var committed = (SqliteTransaction)await connection.BeginTransactionAsync();
        await committed.CommitAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => committed.RollbackAsync());

        await UserDataDbService.RollbackSafelyAsync(committed, "test");
    }

    [Fact]
    public async Task The_watermark_round_trips_and_a_never_reset_profile_answers_null()
    {
        var store = NewStore();
        Assert.Null(await store.GetProgressResetAtAsync(Season));

        var first = new DateTime(2026, 8, 13, 12, 0, 0);
        await store.ResetProfileAsync(Season, first, SettingsService.ProfileKeysSurvivingReset);
        Assert.Equal(first, await store.GetProgressResetAtAsync(Season));
        Assert.Null(await store.GetProgressResetAtAsync(Pve));

        // A second reset simply overwrites the previous watermark.
        var second = new DateTime(2026, 8, 14, 9, 30, 0);
        await store.ResetProfileAsync(Season, second, SettingsService.ProfileKeysSurvivingReset);
        Assert.Equal(second, await store.GetProgressResetAtAsync(Season));
    }
}
