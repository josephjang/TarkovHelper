using System.IO;
using TarkovHelper.Models;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// Pins <see cref="ProgressStoreFake"/> to the key policy of the store it stands in for. Every
/// other test in this project reads its expectations out of the fake, so a fake that stored or
/// returned a shape <c>UserDataDbService</c> never produces would let those tests agree with each
/// other about something production does not do. Two facts matter, and they disagree:
/// a quest row is WRITTEN under its Id and READ BACK under its NormalizedName.
/// </summary>
public sealed class ProgressStoreFakeTests
{
    private const string Profile = "pve";

    private static readonly TarkovTask Quest = TestTasks.Quest("q-1", "a-quest");

    [Fact]
    public async Task A_row_saved_under_its_id_loads_back_under_its_normalized_name()
    {
        var store = new ProgressStoreFake();

        await store.SaveQuestProgressAsync("q-1", "a-quest", QuestStatus.Done, Profile);
        var loaded = await store.LoadQuestProgressAsync(Profile);

        Assert.Equal(QuestStatus.Done, loaded["a-quest"]);
        Assert.False(loaded.ContainsKey("q-1"),
            "the loaded shape is Id-keyed; UserDataDbService returns NormalizedName ?? Id");

        // The Id has not been thrown away: it is the row's identity, and what a delete matches.
        Assert.Equal("a-quest", store.QuestRowsOf(Profile)["q-1"].NormalizedName);
    }

    [Fact]
    public async Task A_row_saved_without_a_normalized_name_loads_back_under_its_id()
    {
        var store = new ProgressStoreFake();

        await store.SaveQuestProgressBatchAsync(
            new[] { ("q-1", (string?)null, QuestStatus.Failed) }, Profile);

        Assert.Equal(QuestStatus.Failed, (await store.LoadQuestProgressAsync(Profile))["q-1"]);
    }

    [Fact]
    public void Seeding_a_quest_stores_the_row_that_quest_would_be_written_as()
    {
        var store = new ProgressStoreFake();

        store.Seed(Profile, Quest, QuestStatus.Done);

        Assert.Equal(QuestStatus.Done, store.QuestsOf(Profile)["a-quest"]);
        Assert.Equal("q-1", store.QuestRowsOf(Profile)["q-1"].Id);
    }

    // The real DELETE matches (Id = @id OR NormalizedName = @id) so a reset issued with the Id
    // still removes a legacy row recorded under the name alone, back when quests had no Ids.
    [Fact]
    public async Task A_delete_keyed_by_the_id_removes_a_legacy_row_stored_under_its_name()
    {
        var store = new ProgressStoreFake();
        // A legacy row: the name IS the Id column, and there is no Id to match on.
        store.Seed(Profile, ("a-quest", null, QuestStatus.Done));

        await store.DeleteQuestProgressAsync("q-1", Profile);
        Assert.Equal(QuestStatus.Done, store.QuestsOf(Profile)["a-quest"]);

        await store.DeleteQuestProgressAsync("a-quest", Profile);
        Assert.Empty(store.QuestsOf(Profile));
    }

    [Fact]
    public async Task A_delete_keyed_by_the_name_removes_a_row_stored_under_its_id()
    {
        var store = new ProgressStoreFake();
        store.Seed(Profile, Quest, QuestStatus.Done);

        await store.DeleteQuestProgressAsync("a-quest", Profile);

        Assert.Empty(store.QuestsOf(Profile));
        Assert.Equal((Profile, "a-quest"), Assert.Single(store.QuestDeletes));
    }

    [Fact]
    public async Task Every_mutating_method_waits_on_the_save_gate()
    {
        var store = new ProgressStoreFake();
        var gated = new List<string>();
        store.SaveGate = profileId =>
        {
            gated.Add(profileId);
            return Task.CompletedTask;
        };

        await store.SaveQuestProgressAsync("q-1", "a-quest", QuestStatus.Done, Profile);
        await store.SaveQuestProgressBatchAsync(
            new[] { ("q-2", (string?)"b-quest", QuestStatus.Done) }, Profile);
        await store.DeleteQuestProgressAsync("q-1", Profile);
        await store.SaveObjectiveProgressAsync("o-1", "q-1", true, Profile);
        await store.DeleteObjectiveProgressAsync("o-1", Profile);

        // A path that skipped the gate would make a "held write across a profile switch" test
        // pass without the write ever being held.
        Assert.Equal(5, gated.Count);
        Assert.All(gated, profileId => Assert.Equal(Profile, profileId));
    }

    [Fact]
    public async Task Deletes_are_recorded_with_the_profile_they_named()
    {
        var store = new ProgressStoreFake();
        store.Seed(Profile, Quest, QuestStatus.Done);
        store.Seed("season", Quest, QuestStatus.Done);

        await store.DeleteQuestProgressAsync("q-1", Profile);

        Assert.Equal((Profile, "q-1"), Assert.Single(store.QuestDeletes));
        Assert.Equal(QuestStatus.Done, store.QuestsOf("season")[Quest.NormalizedName!]);
    }

    // The fake's watermark mirrors the real store's app.progressResetAt row: absent means
    // "never reset" and answers null, seeded means that exact moment comes back.
    [Fact]
    public async Task The_reset_watermark_round_trips_and_defaults_to_null()
    {
        var store = new ProgressStoreFake();
        Assert.Null(await store.GetProgressResetAtAsync(Profile));

        var resetAt = new DateTime(2026, 8, 13, 21, 30, 0);
        store.ResetWatermarks[Profile] = resetAt;

        Assert.Equal(resetAt, await store.GetProgressResetAtAsync(Profile));
        Assert.Null(await store.GetProgressResetAtAsync("season"));
    }

    // Enumerating a partition while a writer mutates it used to throw an intermittent
    // InvalidOperationException that read as a flake. The accessors copy under the same lock the
    // writers take.
    [Fact]
    public async Task Reading_a_partition_while_it_is_written_does_not_throw()
    {
        var store = new ProgressStoreFake();
        using var done = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var writer = Task.Run(async () =>
        {
            for (var i = 0; !done.IsCancellationRequested && i < 2_000; i++)
                await store.SaveQuestProgressAsync($"q-{i}", $"n-{i}", QuestStatus.Done, Profile);
        });

        var reader = Task.Run(() =>
        {
            while (!writer.IsCompleted)
            {
                store.QuestsOf(Profile);
                store.QuestRowsOf(Profile);
            }
        });

        await Task.WhenAll(writer, reader);
        Assert.NotEmpty(store.QuestsOf(Profile));
    }

    /// <summary>
    /// The fake's policy is only worth pinning while it still mirrors the real store's. These are
    /// the two lines it is a model of; if either changes, the expectations above are stale and
    /// every test that reads them is measuring the wrong thing.
    /// </summary>
    [Fact]
    public void The_real_store_still_reads_by_name_first_and_deletes_by_either_spelling()
    {
        var source = File.ReadAllText(Path.Combine(
            TestRepo.Root(), "TarkovHelper", "Services", "UserDataDbService.cs"));

        Assert.Contains("var key = normalizedName ?? id;", source);
        Assert.Contains(
            "DELETE FROM QuestProgress WHERE (Id = @id OR NormalizedName = @id) AND ProfileId = @profileId",
            source);
        Assert.Contains("ON CONFLICT(ProfileId, Id) DO UPDATE SET", source);
    }
}
