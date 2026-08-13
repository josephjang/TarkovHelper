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
public sealed class ProfileResetStoreTests : IDisposable
{
    private const string Season = "season";
    private const string Pve = "pve";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "tarkovhelper-reset-" + Guid.NewGuid().ToString("N"));

    public ProfileResetStoreTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private UserDataDbService NewStore()
        => new(Path.Combine(_root, Guid.NewGuid().ToString("N") + ".db"));

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

    [Fact]
    public async Task A_failure_before_the_commit_rolls_back_everything_including_the_watermark()
    {
        var store = NewStore();
        await SeedProfileAsync(store, Season);
        store.BeforeResetCommitAsync = () => throw new InvalidOperationException("disk full");

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ResetProfileAsync(
            Season, new DateTime(2026, 8, 13, 12, 0, 0), SettingsService.ProfileKeysSurvivingReset));

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

    [Fact]
    public async Task A_pre_upgrade_database_gains_the_owner_column_with_existing_rows_as_legacy()
    {
        var dbPath = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".db");

        // The RaidHistory schema as it was before ownership existed, with one row in it.
        await using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
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
