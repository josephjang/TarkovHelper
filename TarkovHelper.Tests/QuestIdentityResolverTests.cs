using System.IO;
using TarkovDBEditor.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// The rules that decide which quests exist and which row key each one keeps.
/// <para>
/// Every case here is a real shape from the 1.1 refresh: the 91 renames whose progress has to
/// survive, the eight titles that changed owner, the ten pages two or three game records claim,
/// the eighteen seasonal quests the API does not carry, and the 47 Arena pages the wiki category
/// pulls in. Getting any of them wrong is silent in the database and loud in the field, which is
/// why the resolver is a pure function tested against fixtures rather than against upstream.
/// </para>
/// </summary>
public sealed class QuestIdentityResolverTests
{
    #region Matching

    [Fact]
    public void Matches_a_page_by_its_wiki_link()
    {
        var task = Task_("5c0bde0986f77479cf22c2f8", "shooter-born-in-heaven", link: "Shooter_Born_in_Heaven");

        var resolution = Resolve(Pages("Shooter Born in Heaven"), new[] { task });

        var quest = Assert.Single(resolution.Quests);
        Assert.Equal("Shooter Born in Heaven", quest.Title);
        Assert.Same(task, quest.Task);
    }

    [Fact]
    public void Matches_a_page_by_normalized_name_when_the_link_does_not_resolve()
    {
        // The wikiLink points somewhere that is not this page, but the slug still lines up.
        var task = Task_("5c0bde0986f77479cf22c2f8", "stirrup", link: "Some_Other_Page");

        var resolution = Resolve(Pages("Stirrup"), new[] { task });

        Assert.Same(task, Assert.Single(resolution.Quests).Task);
    }

    [Fact]
    public void Percent_encoding_in_a_wiki_link_still_matches_the_page()
    {
        var task = Task_("5c0bde0986f77479cf22c2f8", "whats-on-the-flash-drive",
            link: "What%27s_on_the_Flash_Drive%3F");

        var resolution = Resolve(Pages("What's on the Flash Drive?"), new[] { task });

        Assert.Same(task, Assert.Single(resolution.Quests).Task);
    }

    [Fact]
    public void A_task_is_claimed_by_one_page_only()
    {
        // Two pages whose slugs both normalize onto the same task: only one may take it, and
        // the other has no game record, so it is held back rather than sharing the id.
        var task = Task_("5c0bde0986f77479cf22c2f8", "stirrup", link: "Stirrup");

        var resolution = Resolve(Pages("Stirrup", "Stirrup (quest)"), new[] { task });

        var quest = Assert.Single(resolution.Quests);
        Assert.Equal("Stirrup", quest.Title);
        Assert.Equal("Stirrup (quest)", Assert.Single(resolution.HeldBackPages).Title);
    }

    #endregion

    #region Collisions: the order of evidence

    [Fact]
    public void A_faction_pair_behind_one_page_takes_the_bear_record_and_stays_faction_neutral()
    {
        // Drip-Out and Textile ship one page for a BEAR record and a USEC record. The page
        // serves both sides, so the row must not take either record's faction.
        var bear = Task_("5e4d4ac186f774264f758336", "drip-out-part-1", link: "Drip-Out_-_Part_1", faction: "BEAR");
        var usec = Task_("5e4d515e86f77439374a0d62", "drip-out-part-1-usec", link: "Drip-Out_-_Part_1", faction: "USEC");

        var resolution = Resolve(Pages("Drip-Out - Part 1"), new[] { usec, bear });

        var quest = Assert.Single(resolution.Quests);
        Assert.Equal("5e4d4ac186f774264f758336", quest.Task!.Id);
        Assert.True(quest.FactionPairShared);

        var collision = Assert.Single(resolution.Collisions);
        Assert.Equal(CollisionRule.FactionPair, collision.Rule);
        Assert.Equal(2, collision.CandidateTaskIds.Count);
    }

    [Fact]
    public void The_bear_record_wins_even_when_the_usec_id_sorts_first()
    {
        // The four published faction pages all happen to have the lower id on the BEAR side, so
        // a lowest-id rule and a pinned-faction rule agree on today's data. They are not the
        // same rule, and this is the shape that tells them apart.
        var usec = Task_("1e4d515e86f77439374a0d62", "drip-out-part-1-usec", link: "Drip-Out_-_Part_1", faction: "USEC");
        var bear = Task_("9e4d4ac186f774264f758336", "drip-out-part-1", link: "Drip-Out_-_Part_1", faction: "BEAR");

        var resolution = Resolve(Pages("Drip-Out - Part 1"), new[] { usec, bear });

        Assert.Equal("9e4d4ac186f774264f758336", Assert.Single(resolution.Quests).Task!.Id);
    }

    [Fact]
    public void Both_halves_of_a_faction_chain_choose_the_same_faction()
    {
        // Each page is decided on its own, so a rule that took whichever id sorted first could
        // take BEAR for Part 1 and USEC for Part 2. Part 2's prerequisite would then name the
        // BEAR Part 1 record, which this refresh never imported: BuildRequirements drops a
        // prerequisite it cannot resolve without a word, and the app would offer Part 2 to a
        // player who has not started Part 1.
        var oneBear = Task_("1e4d4ac186f774264f758336", "textile-part-1-bear", link: "Textile_-_Part_1", faction: "BEAR");
        var oneUsec = Task_("2e4d515e86f77438b2195244", "textile-part-1-usec", link: "Textile_-_Part_1", faction: "USEC");
        var twoUsec = Task_("3e4d515e86f77438b2195244", "textile-part-2-usec", link: "Textile_-_Part_2", faction: "USEC",
            requires: new[] { "2e4d515e86f77438b2195244" });
        var twoBear = Task_("4e4d4ac186f774264f758336", "textile-part-2-bear", link: "Textile_-_Part_2", faction: "BEAR",
            requires: new[] { "1e4d4ac186f774264f758336" });

        var resolution = Resolve(
            Pages("Textile - Part 1", "Textile - Part 2"),
            new[] { oneBear, oneUsec, twoBear, twoUsec });

        var partOne = resolution.Quests.Single(q => q.Title == "Textile - Part 1");
        var partTwo = resolution.Quests.Single(q => q.Title == "Textile - Part 2");
        Assert.Equal("1e4d4ac186f774264f758336", partOne.Task!.Id);
        Assert.Equal("4e4d4ac186f774264f758336", partTwo.Task!.Id);

        // The prerequisite the chosen Part 2 names is the Part 1 record this refresh imported.
        Assert.Equal(partOne.Task.Id, Assert.Single(partTwo.Task.TaskRequirements).TaskId);
        Assert.All(resolution.Collisions, c => Assert.Equal(CollisionRule.FactionPair, c.Rule));
    }

    [Fact]
    public void A_stale_third_record_does_not_stop_a_page_from_being_a_faction_pair()
    {
        // Ten pages are contested today and any of them can grow a third record. If that turned
        // the page into a one-faction quest, the row would publish Faction = Usec, and the
        // fielded build compares that string for equality: every BEAR player's list would
        // silently lose the quest.
        var bear = Task_("5e4d4ac186f774264f758336", "drip-out-part-1", link: "Drip-Out_-_Part_1", faction: "BEAR");
        var usec = Task_("66151401efb0539ae10875ae", "drip-out-part-1-usec", link: "Drip-Out_-_Part_1", faction: "USEC");
        var stale = Task_("5b4794cb86f774598100d5d4", "drip-out-part-1-old", link: "Drip-Out_-_Part_1");

        var resolution = Resolve(Pages("Drip-Out - Part 1"), new[] { bear, usec, stale });

        var quest = Assert.Single(resolution.Quests);
        Assert.Equal("5e4d4ac186f774264f758336", quest.Task!.Id);
        Assert.True(quest.FactionPairShared);

        var collision = Assert.Single(resolution.Collisions);
        Assert.Equal(CollisionRule.FactionPair, collision.Rule);
        Assert.Equal(3, collision.CandidateTaskIds.Count);
    }

    [Fact]
    public void Among_two_records_for_the_pinned_faction_the_one_another_task_requires_wins()
    {
        // A dead duplicate on the BEAR side must not take the page just because its id sorts
        // first; the record the rest of the game data points at is the live one.
        var deadBear = Task_("1a4d4ac186f774264f758336", "textile-part-1-bear-old", link: "Textile_-_Part_1", faction: "BEAR");
        var liveBear = Task_("9b4d4ac186f774264f758336", "textile-part-1-bear", link: "Textile_-_Part_1", faction: "BEAR");
        var usec = Task_("2e4d515e86f77438b2195244", "textile-part-1-usec", link: "Textile_-_Part_1", faction: "USEC");
        var partTwo = Task_("4e4d4ac186f774264f758336", "textile-part-2-bear", link: "Textile_-_Part_2", faction: "BEAR",
            requires: new[] { "9b4d4ac186f774264f758336" });

        var resolution = Resolve(
            Pages("Textile - Part 1", "Textile - Part 2"),
            new[] { deadBear, liveBear, usec, partTwo });

        var quest = resolution.Quests.Single(q => q.Title == "Textile - Part 1");
        Assert.Equal("9b4d4ac186f774264f758336", quest.Task!.Id);
        Assert.Equal(CollisionRule.FactionPair,
            resolution.Collisions.Single(c => c.Title == "Textile - Part 1").Rule);
    }

    [Fact]
    public void A_record_another_task_requires_beats_one_nothing_requires()
    {
        // The Tarkov Shooter - Part 5 exists twice: the dead record, and the one Part 6 lists as
        // its prerequisite. A plain lowest-id rule would take the dead one and drop Part 6's
        // only prerequisite with it.
        var dead = Task_("5bc4826c86f774106d22d88b", "the-tarkov-shooter-part-5-old", link: "The_Tarkov_Shooter_-_Part_5");
        var live = Task_("5bc4836986f7740c0152911c", "the-tarkov-shooter-part-5", link: "The_Tarkov_Shooter_-_Part_5");
        var partSix = Task_("5bc4856986f77454c317bea7", "the-tarkov-shooter-part-6", link: "The_Tarkov_Shooter_-_Part_6",
            requires: new[] { "5bc4836986f7740c0152911c" });

        var resolution = Resolve(
            Pages("The Tarkov Shooter - Part 5", "The Tarkov Shooter - Part 6"),
            new[] { dead, live, partSix });

        var partFive = resolution.Quests.Single(q => q.Title == "The Tarkov Shooter - Part 5");
        Assert.Equal("5bc4836986f7740c0152911c", partFive.Task!.Id);
        Assert.Equal(CollisionRule.RequiredByAnotherTask,
            resolution.Collisions.Single(c => c.Title == "The Tarkov Shooter - Part 5").Rule);
    }

    [Fact]
    public void Otherwise_the_record_the_previous_row_already_held_wins()
    {
        // Battery Change and friends exist as an old and a re-created record, identical in
        // every field and required by nothing. Keeping the one the user's log events already
        // matched is the only choice that changes nothing for them.
        var recreated = Task_("6a45208043b8d7604d00b8d5", "battery-change-new", link: "Battery_Change");
        var original = Task_("639136df4b15ca31f76bc31f", "battery-change", link: "Battery_Change");

        var resolution = Resolve(
            Pages("Battery Change"),
            new[] { recreated, original },
            previous: new[] { Row("Battery Change", bsgId: "639136df4b15ca31f76bc31f") });

        Assert.Equal("639136df4b15ca31f76bc31f", Assert.Single(resolution.Quests).Task!.Id);
        Assert.Equal(CollisionRule.PreviousRow, Assert.Single(resolution.Collisions).Rule);
    }

    [Fact]
    public void With_nothing_else_to_go_on_the_newest_record_wins()
    {
        // 639136df is 2022-12-08; 6a452080 is 2026-06-29. The first eight hex digits of a game
        // id are its creation time.
        var older = Task_("639136df4b15ca31f76bc31f", "the-price-of-independence-old", link: "The_Price_of_Independence");
        var newer = Task_("6a45208043b8d7604d00b8d5", "the-price-of-independence", link: "The_Price_of_Independence");

        var resolution = Resolve(Pages("The Price of Independence"), new[] { older, newer });

        Assert.Equal("6a45208043b8d7604d00b8d5", Assert.Single(resolution.Quests).Task!.Id);
        Assert.Equal(CollisionRule.NewestId, Assert.Single(resolution.Collisions).Rule);
    }

    [Fact]
    public void A_page_with_one_candidate_is_not_reported_as_a_collision()
    {
        var resolution = Resolve(
            Pages("Stirrup"),
            new[] { Task_("5c0bde0986f77479cf22c2f8", "stirrup", link: "Stirrup") });

        Assert.Empty(resolution.Collisions);
    }

    [Fact]
    public void A_renamed_page_still_takes_the_record_its_previous_row_holds()
    {
        // Step 3's evidence is "the record the user's progress has been matching". Looking it
        // up by the page title cannot find it once the patch renames the page, and the page
        // then falls through to the newest record and mints a fresh key: progress detaches
        // twice over.
        var held = Task_("639136df4b15ca31f76bc31f", "battery-change", link: "New_Title");
        var newer = Task_("6a45208043b8d7604d00b8d5", "battery-change-new", link: "New_Title");
        var previous = Row("Old Title", bsgId: "639136df4b15ca31f76bc31f");

        var resolution = Resolve(Pages("New Title"), new[] { newer, held }, new[] { previous });

        var quest = Assert.Single(resolution.Quests);
        Assert.Equal("639136df4b15ca31f76bc31f", quest.Task!.Id);
        Assert.Equal(CollisionRule.PreviousRow, Assert.Single(resolution.Collisions).Rule);
        Assert.Equal(previous.Id, quest.Id);
        Assert.True(quest.IdentityCarried);
    }

    [Fact]
    public void A_page_whose_title_changed_hands_does_not_take_the_old_owners_record()
    {
        // The Sew it Good rotation with both records claiming the page. The row named
        // "Sew it Good - Part 2" holds the stale record; the live record is held by the row
        // that was Part 4, and that is the quest the page describes now. A title lookup
        // answers with the old owner and files one quest's recorded progress against another.
        var live = Task_("5ae4497b86f7744cf402ed00", "sew-it-good-part-2", link: "Sew_it_Good_-_Part_2");
        var stale = Task_("5ae4400c86f77406f4f4ba0d", "sew-it-good-part-2-old", link: "Sew_it_Good_-_Part_2");
        var wasPartFour = Row("Sew it Good - Part 4", bsgId: "5ae4497b86f7744cf402ed00");
        var oldPartTwo = Row("Sew it Good - Part 2", bsgId: "5ae4400c86f77406f4f4ba0d");

        var resolution = Resolve(
            Pages("Sew it Good - Part 2"),
            new[] { live, stale },
            new[] { wasPartFour, oldPartTwo });

        var quest = Assert.Single(resolution.Quests);
        Assert.Equal("5ae4497b86f7744cf402ed00", quest.Task!.Id);
        Assert.Equal("Sew it Good - Part 4", quest.PreviousName);
        Assert.Equal(wasPartFour.Id, quest.Id);
    }

    #endregion

    #region Identity carry-over

    [Fact]
    public void A_renamed_quest_keeps_its_row_key_and_its_normalized_name()
    {
        // "A Shooter Born in Heaven" became "Shooter Born in Heaven". Progress is filed under
        // the old normalized name, so both have to survive the rename.
        var task = Task_("5c0bde0986f77479cf22c2f8", "shooter-born-in-heaven", link: "Shooter_Born_in_Heaven");
        var previous = Row("A Shooter Born in Heaven", bsgId: "5c0bde0986f77479cf22c2f8");

        var resolution = Resolve(Pages("Shooter Born in Heaven"), new[] { task }, new[] { previous });

        var quest = Assert.Single(resolution.Quests);
        Assert.Equal("Shooter Born in Heaven", quest.Title);
        Assert.Equal(previous.Id, quest.Id);
        Assert.Equal("a-shooter-born-in-heaven", quest.NormalizedName);
        Assert.True(quest.IdentityCarried);

        var rename = Assert.Single(resolution.Renames);
        Assert.Equal("A Shooter Born in Heaven", rename.PreviousName);
        Assert.False(rename.TitleReused);
    }

    [Fact]
    public void A_previous_database_without_the_column_still_pins_the_value_both_builds_computed()
    {
        // The first 1.1 run starts from a database published before the column existed, where
        // NormalizedName is null. The value to keep is what the app derived from the row's own
        // name, because that is what its stored progress is keyed by.
        var task = Task_("5c0bde0986f77479cf22c2f8", "shooter-born-in-heaven", link: "Shooter_Born_in_Heaven");
        var previous = Row("A Shooter Born in Heaven", bsgId: "5c0bde0986f77479cf22c2f8", normalizedName: null);

        var resolution = Resolve(Pages("Shooter Born in Heaven"), new[] { task }, new[] { previous });

        Assert.Equal("a-shooter-born-in-heaven", Assert.Single(resolution.Quests).NormalizedName);
    }

    [Fact]
    public void A_stored_normalized_name_wins_over_one_derived_from_the_name()
    {
        // Once the column exists it is the record of what progress was filed under, even if the
        // row's name has since moved on. Re-deriving would drop that history.
        var task = Task_("5c0bde0986f77479cf22c2f8", "shooter-born-in-heaven", link: "Shooter_Born_in_Heaven");
        var previous = Row("Shooter Born in Heaven",
            bsgId: "5c0bde0986f77479cf22c2f8",
            normalizedName: "a-shooter-born-in-heaven");

        var resolution = Resolve(Pages("Shooter Born in Heaven"), new[] { task }, new[] { previous });

        Assert.Equal("a-shooter-born-in-heaven", Assert.Single(resolution.Quests).NormalizedName);
    }

    [Fact]
    public void A_new_page_gets_a_fresh_identity_minted_from_its_title()
    {
        var task = Task_("68e4a3f0a1b2c3d4e5f60718", "hiking", link: "Hiking");

        var resolution = Resolve(Pages("Hiking"), new[] { task });

        var quest = Assert.Single(resolution.Quests);
        Assert.Equal(WikiQuestIdentity.IdFor("Hiking"), quest.Id);
        Assert.Equal("hiking", quest.NormalizedName);
        Assert.False(quest.IdentityCarried);
        Assert.Empty(resolution.Renames);
    }

    [Fact]
    public void A_reused_title_moves_with_its_task_and_is_flagged()
    {
        // The Sew it Good rotation: the quest that was Part 4 is now Part 2, and the Part 4 page
        // now belongs to the quest that was Part 3. Keying by page would have put the old Part
        // 4's completion onto a quest the player has not done.
        var wasPartFour = Task_("5ae4497b86f7744cf402ed00", "sew-it-good-part-2", link: "Sew_it_Good_-_Part_2");
        var wasPartThree = Task_("5ae4496986f774459e77beb6", "sew-it-good-part-4", link: "Sew_it_Good_-_Part_4");

        var previousPartFour = Row("Sew it Good - Part 4", bsgId: "5ae4497b86f7744cf402ed00");
        var previousPartThree = Row("Sew it Good - Part 3", bsgId: "5ae4496986f774459e77beb6");

        var resolution = Resolve(
            Pages("Sew it Good - Part 2", "Sew it Good - Part 4"),
            new[] { wasPartFour, wasPartThree },
            new[] { previousPartFour, previousPartThree });

        var partTwo = resolution.Quests.Single(q => q.Title == "Sew it Good - Part 2");
        Assert.Equal(previousPartFour.Id, partTwo.Id);
        Assert.Equal("sew-it-good---part-4", partTwo.NormalizedName);

        var partFour = resolution.Quests.Single(q => q.Title == "Sew it Good - Part 4");
        Assert.Equal(previousPartThree.Id, partFour.Id);
        Assert.Equal("sew-it-good---part-3", partFour.NormalizedName);

        var reuse = Assert.Single(resolution.TitleReuses);
        Assert.Equal("Sew it Good - Part 4", reuse.PreviousName);
        Assert.Equal("Sew it Good - Part 2", reuse.Title);
    }

    [Fact]
    public void A_seasonal_quest_keeps_the_row_key_published_under_its_own_title()
    {
        // The eighteen seasonal quests have no game record and therefore no external id to
        // match on, so they were re-minting their key and normalized name from the current
        // title on every run: the pre-1.1 behaviour, on the quests least able to survive it.
        // The row published under exactly this title is the one the progress is filed against.
        var previous = CarriedRow(
            name: "Uninvited Guests - Part 1",
            mintedFrom: "KORD Breach - Part 1",
            normalizedName: "kord-breach---part-1");

        var resolution = Resolve(
            Seasonal("Uninvited Guests - Part 1"),
            Array.Empty<TarkovDevQuestCacheItem>(),
            new[] { previous });

        var quest = Assert.Single(resolution.Quests);
        Assert.True(quest.IsWikiOnly);
        Assert.Equal(previous.Id, quest.Id);
        Assert.Equal("kord-breach---part-1", quest.NormalizedName);
        Assert.True(quest.IdentityCarried);
        Assert.Empty(resolution.Renames);
    }

    [Fact]
    public void A_seasonal_quest_carried_from_a_database_without_the_column_keeps_its_own_name()
    {
        // Same path from a database published before NormalizedName existed: the value to keep
        // is what the app derived from the row's name, not what the page title derives to.
        var previous = CarriedRow(
            name: "Uninvited Guests - Part 1",
            mintedFrom: "KORD Breach - Part 1",
            normalizedName: null);

        var resolution = Resolve(
            Seasonal("Uninvited Guests - Part 1"),
            Array.Empty<TarkovDevQuestCacheItem>(),
            new[] { previous });

        var quest = Assert.Single(resolution.Quests);
        Assert.Equal(previous.Id, quest.Id);
        Assert.Equal("uninvited-guests---part-1", quest.NormalizedName);
    }

    [Fact]
    public void A_seasonal_page_does_not_take_a_row_whose_game_record_still_exists()
    {
        // Title reuse, the dangerous shape: the row named "Sew it Good - Part 4" belongs to a
        // quest the game still has under another page, and the page with that title today is a
        // different, seasonal quest. Carrying by title would file one quest's completion
        // against the other, and both quests would claim one primary key.
        var task = Task_("5ae4497b86f7744cf402ed00", "sew-it-good-part-2", link: "Sew_it_Good_-_Part_2");
        var previous = CarriedRow(
            name: "Sew it Good - Part 4",
            mintedFrom: "Sew it Good - Part 5",
            bsgId: "5ae4497b86f7744cf402ed00");

        var resolution = Resolve(
            Pages("Sew it Good - Part 2").Concat(Seasonal("Sew it Good - Part 4")).ToArray(),
            new[] { task },
            new[] { previous });

        var partTwo = resolution.Quests.Single(q => q.Title == "Sew it Good - Part 2");
        var partFour = resolution.Quests.Single(q => q.Title == "Sew it Good - Part 4");
        Assert.Equal(previous.Id, partTwo.Id);
        Assert.Equal(WikiQuestIdentity.IdFor("Sew it Good - Part 4"), partFour.Id);
        Assert.NotEqual(partTwo.Id, partFour.Id);
        Assert.False(partFour.IdentityCarried);
    }

    [Fact]
    public void A_seasonal_page_leaves_a_row_alone_while_the_task_set_still_carries_its_record()
    {
        // The record exists but matched no page this run. That is a matching problem to look at
        // in the report, not evidence that the page and the row are the same quest, so the
        // resolver declines to guess rather than risking the wrong attachment.
        var stillListed = Task_("5ae4497b86f7744cf402ed00", "some-other-quest", link: "Some_Other_Page");
        var previous = CarriedRow(
            name: "Uninvited Guests - Part 1",
            mintedFrom: "KORD Breach - Part 1",
            bsgId: "5ae4497b86f7744cf402ed00");

        var resolution = Resolve(
            Seasonal("Uninvited Guests - Part 1"),
            new[] { stillListed },
            new[] { previous });

        var quest = Assert.Single(resolution.Quests);
        Assert.False(quest.IdentityCarried);
        Assert.Equal(WikiQuestIdentity.IdFor("Uninvited Guests - Part 1"), quest.Id);
    }

    [Fact]
    public void Two_quests_that_would_share_one_row_key_stop_the_refresh()
    {
        // The quest that used to be Part 4 keeps the key minted from that title, and the quest
        // that took the title is new, so it mints the same key for itself. Quests.Id is a
        // primary key: one row would overwrite the other, and one quest would leave every
        // install without anything looking wrong.
        var renamed = Task_("5ae4497b86f7744cf402ed00", "sew-it-good-part-2", link: "Sew_it_Good_-_Part_2");
        var newcomer = Task_("6a4d4ac186f774264f758336", "sew-it-good-part-4", link: "Sew_it_Good_-_Part_4");
        var previous = Row("Sew it Good - Part 4", bsgId: "5ae4497b86f7744cf402ed00");

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(
            Pages("Sew it Good - Part 2", "Sew it Good - Part 4"),
            new[] { renamed, newcomer },
            new[] { previous }));

        Assert.Contains("Sew it Good - Part 2", ex.Message);
        Assert.Contains("Sew it Good - Part 4", ex.Message);
    }

    [Fact]
    public void A_previous_row_without_an_external_id_cannot_be_carried()
    {
        // Every published row is in this state until the backfill runs, which is why the
        // refresh refuses to start on an unbackfilled database.
        var task = Task_("5c0bde0986f77479cf22c2f8", "shooter-born-in-heaven", link: "Shooter_Born_in_Heaven");
        var previous = Row("A Shooter Born in Heaven", bsgId: null);

        var resolution = Resolve(Pages("Shooter Born in Heaven"), new[] { task }, new[] { previous });

        var quest = Assert.Single(resolution.Quests);
        Assert.Equal(WikiQuestIdentity.IdFor("Shooter Born in Heaven"), quest.Id);
        Assert.NotEqual(previous.Id, quest.Id);
    }

    [Fact]
    public void A_renamed_seasonal_quest_names_the_row_it_could_not_carry()
    {
        // A seasonal page has no game record and therefore no id to match on, so a rename
        // breaks the only link it has: the exact title. Nothing can carry it automatically, so
        // the run has to say which row is being abandoned instead of re-minting in silence.
        var previous = CarriedRow(name: "KORD Breach - Part 1", mintedFrom: "KORD Breach - Part 1");

        var resolution = Resolve(
            Seasonal("Uninvited Guests - Part 1"),
            Array.Empty<TarkovDevQuestCacheItem>(),
            new[] { previous });

        Assert.False(Assert.Single(resolution.Quests).IdentityCarried);
        var abandoned = Assert.Single(resolution.UncarriedPreviousRows);
        Assert.Equal("KORD Breach - Part 1", abandoned.Name);
        Assert.Null(abandoned.BsgId);
    }

    [Fact]
    public void A_row_a_quest_kept_the_key_of_is_not_reported_as_uncarried()
    {
        var task = Task_("5c0bde0986f77479cf22c2f8", "shooter-born-in-heaven", link: "Shooter_Born_in_Heaven");
        var previous = Row("A Shooter Born in Heaven", bsgId: "5c0bde0986f77479cf22c2f8");

        var resolution = Resolve(Pages("Shooter Born in Heaven"), new[] { task }, new[] { previous });

        Assert.Empty(resolution.UncarriedPreviousRows);
    }

    #endregion

    #region Liveness

    [Fact]
    public void A_page_with_no_game_record_is_held_back()
    {
        // The wiki's quest category also holds 47 pages for the separate Arena game, which the
        // API does not carry and the app has never shown.
        var resolution = Resolve(Pages("Arena: First Blood"), Array.Empty<TarkovDevQuestCacheItem>());

        Assert.Empty(resolution.Quests);
        var heldBack = Assert.Single(resolution.HeldBackPages);
        Assert.Equal("Arena: First Blood", heldBack.Title);
        Assert.Contains("no game record", heldBack.Reason);
    }

    [Fact]
    public void A_seasonal_page_is_imported_on_the_wikis_word_alone()
    {
        var resolution = Resolve(
            new[] { new WikiQuestPage { Title = "Uninvited Guests - Part 1", IsSeasonal = true } },
            Array.Empty<TarkovDevQuestCacheItem>());

        var quest = Assert.Single(resolution.Quests);
        Assert.True(quest.IsWikiOnly);
        Assert.Null(quest.Task);
        Assert.Equal("Uninvited Guests - Part 1", Assert.Single(resolution.WikiOnlyPages));
        Assert.Empty(resolution.HeldBackPages);
    }

    [Fact]
    public void A_seasonal_page_the_API_has_caught_up_with_takes_the_game_record()
    {
        // The exception retires itself: once the API carries the quest, the page matches by
        // link, the row key is unchanged (same page) and the external ID fills in.
        var task = Task_("6900000086f77479cf22c2f8", "uninvited-guests-part-1", link: "Uninvited_Guests_-_Part_1");

        var resolution = Resolve(
            new[] { new WikiQuestPage { Title = "Uninvited Guests - Part 1", IsSeasonal = true } },
            new[] { task });

        var quest = Assert.Single(resolution.Quests);
        Assert.False(quest.IsWikiOnly);
        Assert.Equal(WikiQuestIdentity.IdFor("Uninvited Guests - Part 1"), quest.Id);
        Assert.Empty(resolution.WikiOnlyPages);
    }

    [Fact]
    public void A_prestige_page_with_neither_a_record_nor_a_marker_leaves_the_app()
    {
        // New Beginning (Prestige 5) and (Prestige 6): the API stops at Prestige 4, and the
        // pages carry no seasonal line. Recorded progress stays in the user's data and comes
        // back when the API does.
        var resolution = Resolve(Pages("New Beginning (Prestige 5)"), Array.Empty<TarkovDevQuestCacheItem>());

        Assert.Empty(resolution.Quests);
        Assert.Equal("New Beginning (Prestige 5)", Assert.Single(resolution.HeldBackPages).Title);
    }

    [Fact]
    public void The_wiki_only_list_holds_every_imported_page_without_a_game_record_and_nothing_else()
    {
        var task = Task_("5c0bde0986f77479cf22c2f8", "stirrup", link: "Stirrup");

        var resolution = Resolve(
            Pages("Stirrup").Concat(Seasonal("Uninvited Guests - Part 1", "Uninvited Guests - Part 2")).ToArray(),
            new[] { task });

        Assert.Equal(3, resolution.Quests.Count);
        Assert.Equal(
            new[] { "Uninvited Guests - Part 1", "Uninvited Guests - Part 2" },
            resolution.WikiOnlyPages);
    }

    [Fact]
    public void A_record_with_no_page_is_reported_rather_than_materialized()
    {
        // The API still lists 35 quests the game removed, plus two live ones the wiki has no
        // page for. Neither becomes a row; both show up in the report.
        var removed = Task_("5936d90786f7742b1420ba5b", "the-huntsman-path-control", link: "The_Huntsman_Path_-_Control");

        var resolution = Resolve(Array.Empty<WikiQuestPage>(), new[] { removed });

        Assert.Empty(resolution.Quests);
        var orphan = Assert.Single(resolution.TasksWithoutPage);
        Assert.Equal("5936d90786f7742b1420ba5b", orphan.TaskId);
    }

    #endregion

    #region Alias list

    [Fact]
    public void An_alias_bridges_a_record_whose_link_points_at_a_page_that_does_not_exist()
    {
        // The three prestige records all link to the German title Neuanfang, and their slugs
        // (new-beginning-2) do not match the pages (new-beginning-prestige-2) either.
        var task = Task_("6761ff17cdc36bd66102e9d0", "new-beginning-2", link: "Neuanfang");

        var resolution = Resolve(
            Pages("New Beginning (Prestige 2)"),
            new[] { task },
            overrides: new[] { Override("New Beginning (Prestige 2)", "6761ff17cdc36bd66102e9d0") });

        Assert.Same(task, Assert.Single(resolution.Quests).Task);
        Assert.Equal("New Beginning (Prestige 2)", Assert.Single(resolution.AliasesUsed));
        Assert.Empty(resolution.UnusedAliases);
    }

    [Fact]
    public void An_alias_whose_page_now_matches_on_its_own_is_reported_for_removal()
    {
        // Upstream fixed the link. The entry no longer fires, and the report says so rather
        // than letting it outlive its reason.
        var task = Task_("6761ff17cdc36bd66102e9d0", "new-beginning-prestige-2",
            link: "New_Beginning_(Prestige_2)");

        var resolution = Resolve(
            Pages("New Beginning (Prestige 2)"),
            new[] { task },
            overrides: new[] { Override("New Beginning (Prestige 2)", "6761ff17cdc36bd66102e9d0") });

        Assert.Same(task, Assert.Single(resolution.Quests).Task);
        Assert.Empty(resolution.AliasesUsed);
        Assert.Equal("New Beginning (Prestige 2)", Assert.Single(resolution.UnusedAliases).PageTitle);
    }

    [Fact]
    public void An_alias_naming_a_record_that_no_longer_exists_does_not_import_the_page()
    {
        var resolution = Resolve(
            Pages("New Beginning (Prestige 2)"),
            Array.Empty<TarkovDevQuestCacheItem>(),
            overrides: new[] { Override("New Beginning (Prestige 2)", "6761ff17cdc36bd66102e9d0") });

        Assert.Empty(resolution.Quests);
        Assert.Single(resolution.HeldBackPages);
        Assert.Single(resolution.UnusedAliases);
    }

    [Fact]
    public void The_committed_alias_list_is_well_formed()
    {
        // A malformed list has to fail here rather than halfway through a regeneration.
        var path = Path.Combine(
            TestRepo.Root(), "TarkovDBEditor", "Resources", "Data", QuestMatchOverrides.FileName);
        Assert.True(File.Exists(path), $"{path} is missing");

        var entries = QuestMatchOverrides.Parse(File.ReadAllText(path), path);

        Assert.NotEmpty(entries);
        Assert.All(entries, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.PageTitle));
            Assert.Matches("^[0-9a-f]{24}$", entry.TaskId);
            Assert.False(string.IsNullOrWhiteSpace(entry.UpstreamIssue),
                $"'{entry.PageTitle}' must name the upstream report it waits on so it can be retired.");
        });
    }

    [Fact]
    public void A_taskId_that_is_not_a_game_id_fails_the_list()
    {
        var json = """
            {"overrides":[{"pageTitle":"Somewhere","taskId":"nope","upstreamIssue":"issue-1"}]}
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => QuestMatchOverrides.Parse(json, "test.json"));
        Assert.Contains("24-character game id", ex.Message);
    }

    [Fact]
    public void An_entry_without_an_upstream_issue_fails_the_list()
    {
        var json = """
            {"overrides":[{"pageTitle":"Somewhere","taskId":"6761ff17cdc36bd66102e9d0"}]}
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => QuestMatchOverrides.Parse(json, "test.json"));
        Assert.Contains("upstreamIssue", ex.Message);
    }

    [Fact]
    public void A_page_listed_twice_fails_the_list()
    {
        var json = """
            {"overrides":[
              {"pageTitle":"Somewhere","taskId":"6761ff17cdc36bd66102e9d0","upstreamIssue":"issue-1"},
              {"pageTitle":"Somewhere","taskId":"6848100b00afffa81f09e365","upstreamIssue":"issue-1"}
            ]}
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => QuestMatchOverrides.Parse(json, "test.json"));
        Assert.Contains("twice", ex.Message);
    }

    [Fact]
    public void A_missing_alias_file_fails_the_load_rather_than_matching_nothing()
    {
        // The file is deployed by a copy rule, so it can go missing without anyone editing it.
        // Treating that as "no aliases" would hold back the three prestige pages it bridges,
        // which is far too few quests to trip the lost-match guard: they would just disappear
        // from every install. The pipeline refuses an empty task set and an empty wiki cache
        // for the same reason.
        var path = Path.Combine(Path.GetTempPath(), "no-such-overrides.json");

        var ex = Assert.Throws<InvalidOperationException>(() => QuestMatchOverrides.Load(path));

        Assert.Contains(path, ex.Message);
        Assert.Contains("missing", ex.Message);
    }

    [Fact]
    public void An_alias_file_with_no_entries_is_a_decision_on_record_and_still_loads()
    {
        // Upstream fixing every link empties the list. That is a curator emptying a file, not a
        // file going missing, and it has to stay possible.
        var path = Path.Combine(Path.GetTempPath(), $"empty-overrides-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"overrides":[]}""");

        try
        {
            Assert.Empty(QuestMatchOverrides.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    #endregion

    #region Determinism

    [Fact]
    public void The_same_inputs_in_a_different_order_resolve_the_same_way()
    {
        // A crawl returns pages in whatever order the category listing gives, and two pages can
        // compete for one record. The result must not depend on that.
        var tasks = new[]
        {
            Task_("5c0bde0986f77479cf22c2f8", "stirrup", link: "Stirrup"),
            Task_("5ae4497b86f7744cf402ed00", "sew-it-good-part-2", link: "Sew_it_Good_-_Part_2"),
        };

        var forwards = Resolve(Pages("Stirrup", "Sew it Good - Part 2"), tasks);
        var backwards = Resolve(Pages("Sew it Good - Part 2", "Stirrup"), tasks.Reverse().ToArray());

        Assert.Equal(
            forwards.Quests.Select(q => (q.Title, q.Id, q.Task?.Id)).OrderBy(q => q.Title),
            backwards.Quests.Select(q => (q.Title, q.Id, q.Task?.Id)).OrderBy(q => q.Title));
    }

    #endregion

    #region Item identity

    [Fact]
    public void A_renamed_item_keeps_the_row_key_its_icon_file_is_named_after()
    {
        var previous = new PreviousItemRow { Id = "old-key", Name = "Old Widget", BsgId = "5449016a4bdc2d6f028b456f" };
        var wikiItem = new WikiItemIdentity
        {
            Id = "new-key",
            Name = "New Widget",
            WikiPageLink = "https://escapefromtarkov.fandom.com/wiki/New_Widget",
        };
        var devItems = new Dictionary<string, TarkovDevMultiLangItem>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://escapefromtarkov.fandom.com/wiki/New_Widget"] =
                new() { BsgId = "5449016a4bdc2d6f028b456f", NameEN = "New Widget" },
        };

        var resolution = ItemIdentityResolver.Resolve(new[] { wikiItem }, devItems, new[] { previous });

        Assert.Equal("old-key", resolution.CarriedIds["new-key"]);
        Assert.Equal("Old Widget", Assert.Single(resolution.Renames).PreviousName);
    }

    [Fact]
    public void An_unchanged_item_is_not_reported_as_carried()
    {
        var wikiItem = new WikiItemIdentity
        {
            Id = "same-key",
            Name = "Widget",
            WikiPageLink = "https://escapefromtarkov.fandom.com/wiki/Widget",
        };
        var previous = new PreviousItemRow { Id = "same-key", Name = "Widget", BsgId = "5449016a4bdc2d6f028b456f" };
        var devItems = new Dictionary<string, TarkovDevMultiLangItem>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://escapefromtarkov.fandom.com/wiki/Widget"] =
                new() { BsgId = "5449016a4bdc2d6f028b456f", NameEN = "Widget" },
        };

        var resolution = ItemIdentityResolver.Resolve(new[] { wikiItem }, devItems, new[] { previous });

        Assert.Empty(resolution.CarriedIds);
        Assert.Empty(resolution.Renames);
    }

    [Fact]
    public void One_previous_item_row_can_only_be_carried_onto_one_page()
    {
        // Two wiki pages claiming the same external ID would otherwise collapse into one
        // primary key and lose a row.
        var previous = new PreviousItemRow { Id = "old-key", Name = "Widget", BsgId = "5449016a4bdc2d6f028b456f" };
        var devItem = new TarkovDevMultiLangItem { BsgId = "5449016a4bdc2d6f028b456f", NameEN = "Widget" };
        var devItems = new Dictionary<string, TarkovDevMultiLangItem>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://escapefromtarkov.fandom.com/wiki/Widget_A"] = devItem,
            ["https://escapefromtarkov.fandom.com/wiki/Widget_B"] = devItem,
        };
        var wikiItems = new[]
        {
            new WikiItemIdentity { Id = "key-a", Name = "Widget A", WikiPageLink = "https://escapefromtarkov.fandom.com/wiki/Widget_A" },
            new WikiItemIdentity { Id = "key-b", Name = "Widget B", WikiPageLink = "https://escapefromtarkov.fandom.com/wiki/Widget_B" },
        };

        var resolution = ItemIdentityResolver.Resolve(wikiItems, devItems, new[] { previous });

        Assert.Single(resolution.CarriedIds);
    }

    #endregion

    #region Fixtures

    private static QuestIdentityResolution Resolve(
        IReadOnlyList<WikiQuestPage> pages,
        IReadOnlyList<TarkovDevQuestCacheItem> tasks,
        IReadOnlyList<PreviousQuestRow>? previous = null,
        IReadOnlyList<QuestMatchOverride>? overrides = null) =>
        QuestIdentityResolver.Resolve(pages, tasks, previous ?? Array.Empty<PreviousQuestRow>(), overrides);

    private static WikiQuestPage[] Pages(params string[] titles) =>
        titles.Select(t => new WikiQuestPage { Title = t }).ToArray();

    /// <summary>Pages the wiki marks seasonal: imported even when the API carries no record.</summary>
    private static WikiQuestPage[] Seasonal(params string[] titles) =>
        titles.Select(t => new WikiQuestPage { Title = t, IsSeasonal = true }).ToArray();

    private static TarkovDevQuestCacheItem Task_(
        string id,
        string normalizedName,
        string link,
        string faction = "Any",
        string[]? requires = null) =>
        new()
        {
            Id = id,
            NormalizedName = normalizedName,
            NameEN = normalizedName,
            WikiLink = WikiQuestIdentity.WikiUrlPrefix + link,
            FactionName = faction,
            TaskRequirements = (requires ?? Array.Empty<string>())
                .Select(r => new TarkovDevTaskPrerequisite { TaskId = r, Status = new List<string> { "complete" } })
                .ToList(),
        };

    /// <summary>
    /// A previous row keyed the way every published row is: base64 of the page URL its own name
    /// produces.
    /// </summary>
    private static PreviousQuestRow Row(string name, string? bsgId, string? normalizedName = null) =>
        new()
        {
            Id = WikiQuestIdentity.IdFor(name),
            Name = name,
            BsgId = bsgId,
            NormalizedName = normalizedName,
        };

    /// <summary>
    /// A row an earlier refresh already carried: named for the title the quest has now, keyed
    /// by the title it was first published under. The two only agree until the first rename.
    /// </summary>
    private static PreviousQuestRow CarriedRow(
        string name,
        string mintedFrom,
        string? bsgId = null,
        string? normalizedName = null) =>
        new()
        {
            Id = WikiQuestIdentity.IdFor(mintedFrom),
            Name = name,
            BsgId = bsgId,
            NormalizedName = normalizedName,
        };

    private static QuestMatchOverride Override(string pageTitle, string taskId) =>
        new() { PageTitle = pageTitle, TaskId = taskId, UpstreamIssue = "issue-851" };

    #endregion
}
