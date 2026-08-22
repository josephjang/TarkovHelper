using System.IO;
using DataDiff;
using Microsoft.Data.Sqlite;

namespace TarkovHelper.Tests;

/// <summary>
/// The comparison behind the review artefact.
/// <para>
/// A 1.1 refresh changes essentially every quest row, so the report is what gets read before a
/// publish instead of the database itself. That makes the report's own correctness load-bearing:
/// a rename that reads as one quest removed and another added would bury the eight title reuses
/// in three hundred rows of noise, and a removed column that went unmentioned would break every
/// build in the field.
/// </para>
/// </summary>
public sealed class DataDiffTests : IDisposable
{
    private readonly string _directory;

    public DataDiffTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "datadiff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    #region Quest membership

    [Fact]
    public void A_rename_reads_as_a_rename_not_as_a_removal_and_an_addition()
    {
        var previous = Database("previous.db", quests: new[]
        {
            Quest("key-1", "A Shooter Born in Heaven", bsgId: "5c0bde0986f77479cf22c2f8"),
        });
        var candidate = Database("candidate.db", quests: new[]
        {
            Quest("key-1", "Shooter Born in Heaven", bsgId: "5c0bde0986f77479cf22c2f8"),
        });

        var join = QuestJoin.Build(DataSnapshot.Read(previous), DataSnapshot.Read(candidate));

        Assert.Empty(join.Added);
        Assert.Empty(join.Removed);
        var renamed = Assert.Single(join.Renamed);
        Assert.Equal("A Shooter Born in Heaven", renamed.Previous.Name);
        Assert.Equal(QuestMatchKind.ExternalId, renamed.MatchedBy);
    }

    [Fact]
    public void A_quest_that_kept_its_row_key_but_has_no_external_id_still_matches()
    {
        // Every published quest is in this state before the backfill, and the seasonal quests
        // stay in it until the API carries them.
        var previous = Database("previous.db", quests: new[] { Quest("key-1", "Uninvited Guests - Part 1") });
        var candidate = Database("candidate.db", quests: new[] { Quest("key-1", "Uninvited Guests - Part 1") });

        var join = QuestJoin.Build(DataSnapshot.Read(previous), DataSnapshot.Read(candidate));

        Assert.Equal(QuestMatchKind.RowKey, Assert.Single(join.Pairs).MatchedBy);
    }

    [Fact]
    public void A_title_that_changed_owner_is_called_out()
    {
        // The Sew it Good rotation: the Part 4 page belongs to a different quest than before.
        var previous = Database("previous.db", quests: new[]
        {
            Quest("key-4", "Sew it Good - Part 4", bsgId: "5ae4497b86f7744cf402ed00"),
            Quest("key-3", "Sew it Good - Part 3", bsgId: "5ae4496986f774459e77beb6"),
        });
        var candidate = Database("candidate.db", quests: new[]
        {
            Quest("key-4", "Sew it Good - Part 2", bsgId: "5ae4497b86f7744cf402ed00"),
            Quest("key-3", "Sew it Good - Part 4", bsgId: "5ae4496986f774459e77beb6"),
        });

        var join = QuestJoin.Build(DataSnapshot.Read(previous), DataSnapshot.Read(candidate));

        var reuse = Assert.Single(join.TitleReuses);
        Assert.Equal("Sew it Good - Part 4", reuse.Name);
        Assert.Equal("5ae4497b86f7744cf402ed00", reuse.PreviousBsgId);
        Assert.Equal("5ae4496986f774459e77beb6", reuse.CandidateBsgId);
    }

    [Fact]
    public void Added_and_removed_quests_are_separated()
    {
        var previous = Database("previous.db", quests: new[] { Quest("gone", "Removed Quest", bsgId: "aaaaaaaaaaaaaaaaaaaaaaaa") });
        var candidate = Database("candidate.db", quests: new[] { Quest("fresh", "New Quest", bsgId: "bbbbbbbbbbbbbbbbbbbbbbbb") });

        var join = QuestJoin.Build(DataSnapshot.Read(previous), DataSnapshot.Read(candidate));

        Assert.Equal("New Quest", Assert.Single(join.Added).Name);
        Assert.Equal("Removed Quest", Assert.Single(join.Removed).Name);
        Assert.Empty(join.Pairs);
    }

    #endregion

    #region Report sections

    [Fact]
    public void Reports_a_new_column_and_a_new_table_as_additions()
    {
        var previous = Database("previous.db", quests: new[] { Quest("key-1", "Stirrup") });
        var candidate = Database("candidate.db",
            quests: new[] { Quest("key-1", "Stirrup", normalizedName: "stirrup") },
            withNormalizedName: true,
            withTraderRequirements: true);

        var report = Render(previous, candidate);

        Assert.Contains("Added column `Quests.NormalizedName`", report);
        Assert.Contains("Added table `QuestTraderRequirements`", report);
        Assert.DoesNotContain("**Removed column**", report);
    }

    [Fact]
    public void Calls_a_removed_column_out_as_breaking()
    {
        // The whole data channel rests on the published schema only ever growing, so a removal
        // has to be impossible to skim past.
        var previous = Database("previous.db",
            quests: new[] { Quest("key-1", "Stirrup", normalizedName: "stirrup") },
            withNormalizedName: true);
        var candidate = Database("candidate.db", quests: new[] { Quest("key-1", "Stirrup") });

        var report = Render(previous, candidate);

        Assert.Contains("**Removed column** `Quests.NormalizedName`", report);
        Assert.Contains("breaks every build", report);
    }

    [Fact]
    public void A_dropped_quest_column_is_reported_instead_of_aborting_the_whole_report()
    {
        // The reader used to name all seventeen quest columns in its SELECT, so a candidate that
        // dropped one failed with "no such column" before a single line was written: the report
        // that exists to announce the removal was the thing the removal destroyed.
        var previous = Database("previous.db",
            quests: new[] { Quest("key-1", "Stirrup", bsgId: "5c0be13186f7746309d759c8") });
        var candidate = Database("candidate.db",
            quests: new[] { Quest("key-1", "Stirrup", bsgId: "5c0be13186f7746309d759c8") },
            omitQuestColumns: new[] { "RequiredPrestigeLevel" });

        var report = Render(previous, candidate);

        Assert.Contains("**Removed column** `Quests.RequiredPrestigeLevel`", report);
        // And the rest of the report still rendered, with the quest rows read.
        Assert.Contains("Matched: 1 (of which 1 by external ID)", report);
        Assert.Contains("## NULL rates", report);
    }

    [Fact]
    public void A_dropped_quest_column_reads_as_absent_on_the_side_that_lost_it()
    {
        var previous = Database("previous.db",
            quests: new[] { Quest("key-1", "Stirrup", bsgId: "5c0be13186f7746309d759c8", kappaRequired: true, minLevel: 15) });
        var candidate = Database("candidate.db",
            quests: new[] { Quest("key-1", "Stirrup", bsgId: "5c0be13186f7746309d759c8", kappaRequired: true, minLevel: 15) },
            omitQuestColumns: new[] { "MinLevel", "KappaRequired" });

        var quest = Assert.Single(DataSnapshot.Read(candidate).Quests);
        Assert.Null(quest.MinLevel);
        Assert.False(quest.KappaRequired);

        // The loss shows as a field change too, so it cannot be read as "nothing happened". Each
        // row is pinned to the field that owns it, because the two tables share a row shape.
        var report = Render(previous, candidate);
        Assert.Contains("| Stirrup | 15 | _(none)_ |", Section(report, "### MinLevel"));
        Assert.Contains("| Stirrup | 1 | 0 |", Section(report, "### KappaRequired"));
    }

    [Fact]
    public void A_dropped_item_column_is_reported_instead_of_aborting_the_whole_report()
    {
        var previous = Database("previous.db",
            items: new[] { ("item-1", "Roubles", (string?)"5449016a4bdc2d6f028b456f") });
        var candidate = Database("candidate.db",
            items: new[] { ("item-1", "Roubles", (string?)null) },
            omitItemColumns: new[] { "BsgId" });

        var report = Render(previous, candidate);

        Assert.Contains("**Removed column** `Items.BsgId`", report);
        Assert.Contains("| Items.BsgId | 0/1 (0%) | 1/1 (100%) |", report);
    }

    [Fact]
    public void Objectives_are_read_in_quest_and_sort_order()
    {
        // Ordering moved out of the SQL when the reader stopped naming columns it might not have,
        // so the order the report reads objectives in is worth pinning.
        var database = Database("objectives.db",
            quests: new[] { Quest("key-1", "Stirrup"), Quest("key-0", "Debut") },
            objectives: new[]
            {
                ("key-1", 2, "Third"),
                ("key-0", 0, "Debut objective"),
                ("key-1", 0, "First"),
                ("key-1", 1, "Second"),
            });

        var objectives = DataSnapshot.Read(database).Objectives;

        Assert.Equal(
            new[] { "Debut objective", "First", "Second", "Third" },
            objectives.Select(o => o.Description));
    }

    [Fact]
    public void Lists_every_kappa_and_level_change_in_full()
    {
        var previous = Database("previous.db", quests: new[]
        {
            Quest("key-1", "Stirrup", bsgId: "5c0be13186f7746309d759c8", kappaRequired: true, minLevel: 15),
        });
        var candidate = Database("candidate.db", quests: new[]
        {
            Quest("key-1", "Stirrup", bsgId: "5c0be13186f7746309d759c8", kappaRequired: false, minLevel: null),
        });

        var report = Render(previous, candidate);

        Assert.Contains("### KappaRequired", report);
        Assert.Contains("| Stirrup | 1 | 0 |", Section(report, "### KappaRequired"));
        Assert.Contains("### MinLevel", report);
        Assert.Contains("| Stirrup | 15 | _(none)_ |", Section(report, "### MinLevel"));
    }

    [Fact]
    public void Reports_prerequisite_edges_by_quest_name()
    {
        // Named rather than keyed so an edge that only moved because a row key was reissued does
        // not read as a change.
        var previous = Database("previous.db",
            quests: new[]
            {
                Quest("key-1", "Stirrup", bsgId: "5c0be13186f7746309d759c8"),
                Quest("key-2", "Collector", bsgId: "5c51aac186f77432ea65c552"),
            },
            requirements: new[] { ("key-1", "key-2", "Complete") });
        var candidate = Database("candidate.db",
            quests: new[]
            {
                Quest("key-1", "Stirrup", bsgId: "5c0be13186f7746309d759c8"),
                Quest("key-2", "Collector", bsgId: "5c51aac186f77432ea65c552"),
            });

        var section = Section(Render(previous, candidate), "## Prerequisite edges");

        Assert.Contains("Edges removed: 1", section);
        Assert.Contains("| Stirrup | - | Collector (Complete) |", section);
    }

    [Fact]
    public void Reports_objective_lists_whose_shape_changed()
    {
        var previous = Database("previous.db",
            quests: new[] { Quest("key-1", "Stirrup", bsgId: "5c0be13186f7746309d759c8") },
            objectives: new[] { ("key-1", 0, "Eliminate 3 PMCs with a pistol") });
        var candidate = Database("candidate.db",
            quests: new[] { Quest("key-1", "Stirrup", bsgId: "5c0be13186f7746309d759c8") },
            objectives: new[]
            {
                ("key-1", 0, "Eliminate 10 targets with a pistol on Factory"),
                ("key-1", 1, "Hand over the items"),
            });

        var section = Section(Render(previous, candidate), "## Objective lists whose shape changed");

        // Scoped to the section: "| Stirrup | 1 | 2 |" is also what a MinLevel change from 1 to 2
        // writes, so a Contains over the whole report would pass on the wrong table.
        Assert.Contains("Quests affected: 1", section);
        Assert.Contains("| Stirrup | 1 | 2 |", section);
    }

    [Fact]
    public void Reports_the_loyalty_gates_the_candidate_carries()
    {
        var previous = Database("previous.db", quests: new[] { Quest("key-1", "Chemical - Part 3") });
        var candidate = Database("candidate.db",
            quests: new[] { Quest("key-1", "Chemical - Part 3") },
            withTraderRequirements: true,
            traderGates: new[] { ("key-1", "Jaeger", 2) });

        var report = Render(previous, candidate);

        Assert.Contains("| Chemical - Part 3 | Jaeger LL2 |", report);
    }

    [Fact]
    public void Reports_null_rates_for_the_columns_that_matter()
    {
        var previous = Database("previous.db", quests: new[] { Quest("key-1", "Stirrup") });
        var candidate = Database("candidate.db",
            quests: new[] { Quest("key-1", "Stirrup", bsgId: "5c0be13186f7746309d759c8") });

        var report = Render(previous, candidate);

        Assert.Contains("| Quests.BsgId | 1/1 (100%) | 0/1 (0%) |", report);
    }

    [Fact]
    public void Reports_the_hideout_join_coverage()
    {
        // 0 of 317 rows joined in the published data, because every item's external ID was NULL.
        var previous = Database("previous.db",
            items: new[] { ("item-1", "Roubles", (string?)null) },
            hideoutRequirements: new[] { ("station-1", 1, "5449016a4bdc2d6f028b456f", 400000) });
        var candidate = Database("candidate.db",
            items: new[] { ("item-1", "Roubles", (string?)"5449016a4bdc2d6f028b456f") },
            hideoutRequirements: new[] { ("station-1", 1, "5449016a4bdc2d6f028b456f", 400000) });

        var report = Render(previous, candidate);

        Assert.Contains("0/1 (0%) -> 1/1 (100%)", report);
    }

    [Fact]
    public void Reports_renamed_items_because_they_lose_their_inventory_count()
    {
        var previous = Database("previous.db", items: new[] { ("item-1", "Old Widget", (string?)null) });
        var candidate = Database("candidate.db", items: new[] { ("item-1", "New Widget", (string?)null) });

        var report = Render(previous, candidate);

        Assert.Contains("Renamed (row key kept): 1", report);
        Assert.Contains("| Old Widget | New Widget |", report);
    }

    #endregion

    #region Icon coverage

    [Fact]
    public void Icon_coverage_separates_missing_icons_from_orphan_files()
    {
        var iconDirectory = Path.Combine(_directory, "icons");
        Directory.CreateDirectory(iconDirectory);
        File.WriteAllText(Path.Combine(iconDirectory, "item-1.png"), "");
        File.WriteAllText(Path.Combine(iconDirectory, "orphan.png"), "");
        // A download that produced something other than a PNG is invisible in the database and
        // shows as a blank icon in the app.
        File.WriteAllText(Path.Combine(iconDirectory, "item-2.webp"), "");

        var items = new[]
        {
            new ItemRow("item-1", null, "Has Icon"),
            new ItemRow("item-2", null, "Downloaded As WebP"),
        };

        var coverage = IconCoverage.Measure(items, iconDirectory);

        Assert.True(coverage.DirectoryExists);
        Assert.Equal(1, coverage.ItemsWithIcon);
        Assert.Equal(new[] { "Downloaded As WebP" }, coverage.ItemsWithoutIcon);
        Assert.Equal(new[] { "orphan.png" }, coverage.OrphanFiles);
        Assert.Equal(new[] { "item-2.webp" }, coverage.NonPngFiles);
    }

    [Fact]
    public void A_missing_icon_folder_measures_nothing_rather_than_claiming_a_total_icon_loss()
    {
        // A mistyped --icons used to render as every item in the release having lost its picture,
        // which is a real and alarming outcome. The reviewer then either blocks a good publish or
        // learns to skip the section.
        var coverage = IconCoverage.Measure(
            new[] { new ItemRow("item-1", null, "Widget") },
            Path.Combine(_directory, "no-such-folder"));

        Assert.False(coverage.DirectoryExists);
        Assert.Equal(0, coverage.ItemsWithIcon);
        Assert.Empty(coverage.ItemsWithoutIcon);
    }

    [Fact]
    public void The_report_says_a_missing_icon_folder_was_not_measured()
    {
        var previous = Database("previous.db", items: new[] { ("item-1", "Widget", (string?)null) });
        var candidate = Database("candidate.db", items: new[] { ("item-1", "Widget", (string?)null) });

        var report = DiffReport.Render(
            DataSnapshot.Read(previous),
            DataSnapshot.Read(candidate),
            new DiffOptions { IconDirectory = Path.Combine(_directory, "no-such-folder") });

        Assert.Contains("**Not measured**", report);
        Assert.DoesNotContain("Items without a PNG", report);
        // The per item list is what made the false reading look authoritative.
        Assert.DoesNotContain("- Widget", report);
    }

    [Fact]
    public void The_report_says_when_icons_were_not_checked()
    {
        var previous = Database("previous.db", quests: new[] { Quest("key-1", "Stirrup") });
        var candidate = Database("candidate.db", quests: new[] { Quest("key-1", "Stirrup") });

        Assert.Contains("Not checked (pass `--icons <dir>`)", Render(previous, candidate));
    }

    #endregion

    #region Refresh log

    [Fact]
    public void The_refresh_log_contributes_what_never_reached_the_database()
    {
        var log = RefreshLog.Parse("""
            {
              "writtenAt": "2026-08-22T00:00:00Z",
              "counts": {"quests": 480, "heldBackPages": 49},
              "heldBackPages": [{"Title":"Arena: First Blood","Reason":"no game record"}],
              "wikiOnlySeasonal": ["Uninvited Guests - Part 1"],
              "tasksWithoutPage": [{"TaskId":"5936d90786f7742b1420ba5b","NameEN":"The Huntsman Path - Control"}],
              "collisions": [{"Title":"The Tarkov Shooter - Part 5","CandidateTaskIds":["a","b"],"ChosenTaskId":"b","Rule":"RequiredByAnotherTask"}],
              "prerequisiteDisagreements": [{"Quest":"Sew it Good - Part 4","Verdict":"taskSuperset","Wiki":[],"Game":["Sew it Good - Part 3"]}],
              "unusedAliases": [{"PageTitle":"New Beginning (Prestige 2)","TaskId":"6761ff17cdc36bd66102e9d0","UpstreamIssue":"issue-851"}]
            }
            """, "test.json");

        var previous = Database("previous.db", quests: new[] { Quest("key-1", "Stirrup") });
        var candidate = Database("candidate.db", quests: new[] { Quest("key-1", "Stirrup") });

        var report = DiffReport.Render(
            DataSnapshot.Read(previous),
            DataSnapshot.Read(candidate),
            new DiffOptions { RefreshLog = log });

        Assert.Contains("Uninvited Guests - Part 1", report);
        Assert.Contains("Arena: First Blood", report);
        Assert.Contains("The Huntsman Path - Control", report);
        Assert.Contains("RequiredByAnotherTask", report);
        Assert.Contains("taskSuperset", report);
        Assert.Contains("New Beginning (Prestige 2)", report);
    }

    [Fact]
    public void The_refresh_log_reports_title_reuses_the_database_comparison_cannot_see()
    {
        // The comparison infers a title reuse from two external IDs, so against a previous
        // database written before the backfill it finds none - which is exactly the run where a
        // reuse is most likely. The resolver saw them, and the log is where they survive.
        var log = RefreshLog.Parse("""
            {
              "writtenAt": "2026-08-22T00:00:00Z",
              "renames": [
                {"PreviousName":"Sew it Good - Part 3","Title":"Sew it Good - Part 4","BsgId":"5ae4496986f774459e77beb6","Id":"key-3","TitleReused":true},
                {"PreviousName":"A Shooter Born in Heaven","Title":"Shooter Born in Heaven","BsgId":"5c0bde0986f77479cf22c2f8","Id":"key-9","TitleReused":false}
              ],
              "titleReuses": [
                {"PreviousName":"Sew it Good - Part 3","Title":"Sew it Good - Part 4","BsgId":"5ae4496986f774459e77beb6","Id":"key-3","TitleReused":true}
              ],
              "aliasesUsed": ["New Beginning (Prestige 2)"]
            }
            """, "refresh.json");

        // No external ID on either side, as in every database written before the backfill.
        var previous = Database("previous.db", quests: new[] { Quest("key-3", "Sew it Good - Part 3") });
        var candidate = Database("candidate.db", quests: new[] { Quest("key-3", "Sew it Good - Part 4") });

        var report = DiffReport.Render(
            DataSnapshot.Read(previous),
            DataSnapshot.Read(candidate),
            new DiffOptions { RefreshLog = log });

        Assert.Contains("Titles now belonging to a different quest: 0", report);
        Assert.Contains("### Titles the resolver saw change owner", report);
        Assert.Contains("| Sew it Good - Part 3 | Sew it Good - Part 4 | `5ae4496986f774459e77beb6` | `key-3` |", report);
        Assert.Contains("### Renames the resolver carried", report);
        Assert.Contains("A Shooter Born in Heaven -> Shooter Born in Heaven", report);
        Assert.Contains("### Pages matched only by a hand written alias", report);
        Assert.Contains("- New Beginning (Prestige 2)", report);
    }

    [Fact]
    public void A_refresh_log_that_is_not_json_fails_with_its_name()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => RefreshLog.Parse("<html>", "refresh_1.json"));
        Assert.Contains("refresh_1.json", ex.Message);
    }

    #endregion

    #region Computed section results

    // The three sections below decide what they have to say before they write anything, so the
    // decision is assertable on its own. The markdown assertions elsewhere in this file stay as
    // the guard on the wording; these pin the finding.

    [Fact]
    public void Schema_changes_are_reported_as_findings_not_as_report_length()
    {
        var previous = Database("previous.db",
            quests: new[] { Quest("key-1", "Stirrup", normalizedName: "stirrup") },
            withNormalizedName: true);
        var candidate = Database("candidate.db",
            quests: new[] { Quest("key-1", "Stirrup") },
            withTraderRequirements: true,
            questColumnTypes: new[] { ("MinLevel", "TEXT") });

        var changes = DiffReport.ComputeSchemaChanges(DataSnapshot.Read(previous), DataSnapshot.Read(candidate));

        Assert.Equal(
            new[]
            {
                new SchemaChange(SchemaChangeKind.AddedTable, "QuestTraderRequirements", ColumnCount: 5),
                new SchemaChange(SchemaChangeKind.RemovedColumn, "Quests", "NormalizedName"),
                new SchemaChange(SchemaChangeKind.RetypedColumn, "Quests", "MinLevel", "INTEGER", "TEXT"),
            },
            changes);
    }

    [Fact]
    public void A_retyped_column_is_named_with_the_type_on_each_side()
    {
        // Additive schema growth is the whole contract with the builds in the field, and a retype
        // breaks it exactly as a removal does. No other test in this file writes one.
        var previous = Database("previous.db", quests: new[] { Quest("key-1", "Stirrup") });
        var candidate = Database("candidate.db",
            quests: new[] { Quest("key-1", "Stirrup") },
            questColumnTypes: new[] { ("MinLevel", "TEXT") });

        var report = Render(previous, candidate);

        Assert.Contains(
            "- **Retyped column** `Quests.MinLevel`: INTEGER -> TEXT",
            Section(report, "## Schema delta"));
    }

    [Fact]
    public void Two_identical_schemas_produce_no_findings_and_say_so()
    {
        var previous = Database("previous.db", quests: new[] { Quest("key-1", "Stirrup") });
        var candidate = Database("candidate.db", quests: new[] { Quest("key-1", "Stirrup") });

        Assert.Empty(DiffReport.ComputeSchemaChanges(DataSnapshot.Read(previous), DataSnapshot.Read(candidate)));
        Assert.Equal("\nNo schema change.\n", Section(Render(previous, candidate), "## Schema delta"));
    }

    [Fact]
    public void A_schema_that_did_change_never_also_claims_it_did_not()
    {
        // The two halves of the section are decided by one list, so they cannot contradict.
        var previous = Database("previous.db", quests: new[] { Quest("key-1", "Stirrup") });
        var candidate = Database("candidate.db",
            quests: new[] { Quest("key-1", "Stirrup", normalizedName: "stirrup") },
            withNormalizedName: true);

        Assert.DoesNotContain("No schema change.", Section(Render(previous, candidate), "## Schema delta"));
    }

    [Fact]
    public void Prerequisite_changes_carry_the_edges_gained_and_lost_per_quest()
    {
        var previous = Database("previous.db",
            quests: new[]
            {
                Quest("key-1", "Stirrup", bsgId: "5c0be13186f7746309d759c8"),
                Quest("key-2", "Collector", bsgId: "5c51aac186f77432ea65c552"),
                Quest("key-3", "Debut", bsgId: "5936d90786f7742b1420ba5b"),
            },
            requirements: new[] { ("key-1", "key-2", "Complete"), ("key-3", "key-2", "Complete") });
        var candidate = Database("candidate.db",
            quests: new[]
            {
                Quest("key-1", "Stirrup", bsgId: "5c0be13186f7746309d759c8"),
                Quest("key-2", "Collector", bsgId: "5c51aac186f77432ea65c552"),
                Quest("key-3", "Debut", bsgId: "5936d90786f7742b1420ba5b"),
            },
            // Stirrup's edge is retyped, which reads as one gained and one lost. Debut is
            // untouched and must not appear at all.
            requirements: new[] { ("key-1", "key-2", "Accept"), ("key-3", "key-2", "Complete") });

        var changes = DiffReport.ComputePrerequisiteChanges(DataSnapshot.Read(previous), DataSnapshot.Read(candidate));

        var change = Assert.Single(changes);
        Assert.Equal("Stirrup", change.Quest);
        Assert.Equal(new[] { "Collector (Accept)" }, change.Added);
        Assert.Equal(new[] { "Collector (Complete)" }, change.Removed);
    }

    [Fact]
    public void The_prerequisite_totals_are_the_sums_over_the_changed_quests()
    {
        var previous = Database("previous.db",
            quests: new[]
            {
                Quest("key-1", "Stirrup", bsgId: "5c0be13186f7746309d759c8"),
                Quest("key-2", "Collector", bsgId: "5c51aac186f77432ea65c552"),
                Quest("key-3", "Debut", bsgId: "5936d90786f7742b1420ba5b"),
            },
            requirements: new[] { ("key-1", "key-2", "Complete"), ("key-1", "key-3", "Complete") });
        var candidate = Database("candidate.db",
            quests: new[]
            {
                Quest("key-1", "Stirrup", bsgId: "5c0be13186f7746309d759c8"),
                Quest("key-2", "Collector", bsgId: "5c51aac186f77432ea65c552"),
                Quest("key-3", "Debut", bsgId: "5936d90786f7742b1420ba5b"),
            },
            requirements: new[] { ("key-2", "key-3", "Complete") });

        var changes = DiffReport.ComputePrerequisiteChanges(DataSnapshot.Read(previous), DataSnapshot.Read(candidate));
        var section = Section(Render(previous, candidate), "## Prerequisite edges");

        Assert.Equal(1, changes.Sum(c => c.Added.Count));
        Assert.Equal(2, changes.Sum(c => c.Removed.Count));
        Assert.Contains("- Edges added: 1", section);
        Assert.Contains("- Edges removed: 2", section);
        Assert.Contains("- Quests whose prerequisite list changed: 2", section);
    }

    [Fact]
    public void Two_databases_with_no_prerequisites_at_all_report_zero_and_no_table()
    {
        var previous = Database("previous.db", quests: new[] { Quest("key-1", "Stirrup") });
        var candidate = Database("candidate.db", quests: new[] { Quest("key-1", "Stirrup") });

        Assert.Empty(DiffReport.ComputePrerequisiteChanges(DataSnapshot.Read(previous), DataSnapshot.Read(candidate)));

        var section = Section(Render(previous, candidate), "## Prerequisite edges");
        Assert.Contains("- Edges added: 0", section);
        Assert.Contains("- Quests whose prerequisite list changed: 0", section);
        Assert.DoesNotContain("| Quest | Added | Removed |", section);
    }

    [Fact]
    public void An_objective_list_that_only_changed_wording_is_still_a_shape_change()
    {
        // Equal counts, different text: the tick marks are stored by position, so the row belongs
        // in the section even though neither number moved.
        var previous = Database("previous.db",
            quests: new[] { Quest("key-1", "Stirrup", bsgId: "5c0be13186f7746309d759c8") },
            objectives: new[] { ("key-1", 0, "Eliminate 3 PMCs") });
        var candidate = Database("candidate.db",
            quests: new[] { Quest("key-1", "Stirrup", bsgId: "5c0be13186f7746309d759c8") },
            objectives: new[] { ("key-1", 0, "Eliminate 10 PMCs") });

        var previousSnapshot = DataSnapshot.Read(previous);
        var candidateSnapshot = DataSnapshot.Read(candidate);
        var changes = DiffReport.ComputeObjectiveShapeChanges(
            previousSnapshot, candidateSnapshot, QuestJoin.Build(previousSnapshot, candidateSnapshot));

        Assert.Equal(new ObjectiveShapeChange("Stirrup", 1, 1), Assert.Single(changes));
    }

    [Fact]
    public void An_unchanged_objective_list_is_left_out()
    {
        var previous = Database("previous.db",
            quests: new[] { Quest("key-1", "Stirrup", bsgId: "5c0be13186f7746309d759c8") },
            objectives: new[] { ("key-1", 0, "Eliminate 3 PMCs"), ("key-1", 1, "Hand over the items") });
        var candidate = Database("candidate.db",
            quests: new[] { Quest("key-1", "Stirrup", bsgId: "5c0be13186f7746309d759c8") },
            objectives: new[] { ("key-1", 0, "Eliminate 3 PMCs"), ("key-1", 1, "Hand over the items") });

        var previousSnapshot = DataSnapshot.Read(previous);
        var candidateSnapshot = DataSnapshot.Read(candidate);

        Assert.Empty(DiffReport.ComputeObjectiveShapeChanges(
            previousSnapshot, candidateSnapshot, QuestJoin.Build(previousSnapshot, candidateSnapshot)));
    }

    [Fact]
    public void An_objective_row_is_not_satisfied_by_an_identically_shaped_field_change_row()
    {
        // Stirrup goes from one objective to two AND from MinLevel 1 to MinLevel 2, so the exact
        // text "| Stirrup | 1 | 2 |" is written twice, once by each section. A bare Contains over
        // the whole report cannot tell which section wrote it, and would pass on the wrong one.
        var previous = Database("previous.db",
            quests: new[] { Quest("key-1", "Stirrup", bsgId: "5c0be13186f7746309d759c8", minLevel: 1) },
            objectives: new[] { ("key-1", 0, "Eliminate 3 PMCs with a pistol") });
        var candidate = Database("candidate.db",
            quests: new[] { Quest("key-1", "Stirrup", bsgId: "5c0be13186f7746309d759c8", minLevel: 2) },
            objectives: new[]
            {
                ("key-1", 0, "Eliminate 10 targets with a pistol on Factory"),
                ("key-1", 1, "Hand over the items"),
            });

        var previousSnapshot = DataSnapshot.Read(previous);
        var candidateSnapshot = DataSnapshot.Read(candidate);
        var changes = DiffReport.ComputeObjectiveShapeChanges(
            previousSnapshot, candidateSnapshot, QuestJoin.Build(previousSnapshot, candidateSnapshot));

        Assert.Equal(new ObjectiveShapeChange("Stirrup", 1, 2), Assert.Single(changes));

        var report = Render(previous, candidate);
        var objectives = Section(report, "## Objective lists whose shape changed");
        var minLevel = Section(report, "### MinLevel");

        Assert.Contains("| Stirrup | 1 | 2 |", objectives);
        Assert.Contains("| Stirrup | 1 | 2 |", minLevel);

        // And the two really are separated, so the assertions above are not both reading the
        // same table twice.
        Assert.Contains("| Quest | Previous objectives | Candidate objectives |", objectives);
        Assert.DoesNotContain("| Quest | Previous | Candidate |", objectives);
        Assert.Contains("| Quest | Previous | Candidate |", minLevel);
        Assert.DoesNotContain("| Quest | Previous objectives | Candidate objectives |", minLevel);
    }

    #endregion

    #region Fixtures

    private static string Render(string previousPath, string candidatePath) =>
        DiffReport.Render(DataSnapshot.Read(previousPath), DataSnapshot.Read(candidatePath));

    /// <summary>
    /// The body of one markdown section, from its heading down to the next heading at the same
    /// level or shallower.
    /// <para>
    /// Several sections write a three column row of the form <c>| Quest | a | b |</c>, so a
    /// Contains over the whole report cannot tell an objective count row from a field change row
    /// that happens to hold the same two numbers. An assertion that means "this section says
    /// this" has to be scoped to the section.
    /// </para>
    /// </summary>
    private static string Section(string report, string heading)
    {
        var lines = report.Replace("\r\n", "\n").Split('\n');
        var level = heading.TakeWhile(c => c == '#').Count();
        var start = Array.IndexOf(lines, heading);
        Assert.True(start >= 0, $"The report has no section headed \"{heading}\".");

        var body = new List<string>();
        for (var i = start + 1; i < lines.Length; i++)
        {
            var lineLevel = lines[i].TakeWhile(c => c == '#').Count();
            if (lineLevel > 0 && lineLevel <= level)
                break;

            body.Add(lines[i]);
        }

        return string.Join("\n", body);
    }

    private static (string Id, string Name, string? BsgId, bool Kappa, int? MinLevel, string? NormalizedName) Quest(
        string id,
        string name,
        string? bsgId = null,
        bool kappaRequired = false,
        int? minLevel = null,
        string? normalizedName = null) =>
        (id, name, bsgId, kappaRequired, minLevel, normalizedName);

    /// <summary>
    /// Writes a database in the published shape.
    /// <para>
    /// <paramref name="omitQuestColumns"/> and <paramref name="omitItemColumns"/> leave a column
    /// out of the CREATE TABLE, which is the only way to stand in for a regeneration that dropped
    /// one. A fixture that always creates every column cannot see the failure that matters most
    /// here, because the report is the thing that has to survive a removal in order to name it.
    /// </para>
    /// <para>
    /// <paramref name="questColumnTypes"/> declares a quest column with a different SQLite type,
    /// for the retype the report has to name. A retype breaks a build in the field just as a
    /// removal does, and nothing else in this fixture can write one.
    /// </para>
    /// </summary>
    private string Database(
        string fileName,
        (string Id, string Name, string? BsgId, bool Kappa, int? MinLevel, string? NormalizedName)[]? quests = null,
        (string Id, string Name, string? BsgId)[]? items = null,
        (string QuestId, string RequiredQuestId, string Type)[]? requirements = null,
        (string QuestId, int SortOrder, string Description)[]? objectives = null,
        (string QuestId, string TraderName, int Level)[]? traderGates = null,
        (string StationId, int Level, string ItemId, int Count)[]? hideoutRequirements = null,
        bool withNormalizedName = false,
        bool withTraderRequirements = false,
        string[]? omitQuestColumns = null,
        string[]? omitItemColumns = null,
        (string Column, string Type)[]? questColumnTypes = null)
    {
        var path = Path.Combine(_directory, fileName);
        withNormalizedName |= quests?.Any(q => q.NormalizedName != null) == true;
        withTraderRequirements |= traderGates != null;

        var questColumns = new List<(string Name, string Definition)>
        {
            ("Id", "Id TEXT PRIMARY KEY"),
            ("BsgId", "BsgId TEXT"),
            ("Name", "Name TEXT NOT NULL"),
            ("NameEN", "NameEN TEXT"),
            ("NameKO", "NameKO TEXT"),
            ("NameJA", "NameJA TEXT"),
            ("Trader", "Trader TEXT"),
            ("Location", "Location TEXT"),
            ("MinLevel", "MinLevel INTEGER"),
            ("MinScavKarma", "MinScavKarma INTEGER"),
            ("KappaRequired", "KappaRequired INTEGER NOT NULL DEFAULT 0"),
            ("Faction", "Faction TEXT"),
            ("RequiredEdition", "RequiredEdition TEXT"),
            ("ExcludedEdition", "ExcludedEdition TEXT"),
            ("RequiredPrestigeLevel", "RequiredPrestigeLevel INTEGER"),
            ("RequiredDecodeCount", "RequiredDecodeCount INTEGER"),
        };
        if (withNormalizedName)
            questColumns.Add(("NormalizedName", "NormalizedName TEXT"));

        var itemColumns = new List<(string Name, string Definition)>
        {
            ("Id", "Id TEXT PRIMARY KEY"),
            ("BsgId", "BsgId TEXT"),
            ("Name", "Name TEXT NOT NULL"),
        };

        foreach (var (column, type) in questColumnTypes ?? Array.Empty<(string, string)>())
        {
            var index = questColumns.FindIndex(c => c.Name == column);
            if (index < 0)
                throw new ArgumentException($"Quests has no column named {column}.", nameof(questColumnTypes));

            questColumns[index] = (column, $"{column} {type}");
        }

        var omittedQuestColumns = new HashSet<string>(omitQuestColumns ?? Array.Empty<string>(), StringComparer.Ordinal);
        var omittedItemColumns = new HashSet<string>(omitItemColumns ?? Array.Empty<string>(), StringComparer.Ordinal);
        questColumns.RemoveAll(c => omittedQuestColumns.Contains(c.Name));
        itemColumns.RemoveAll(c => omittedItemColumns.Contains(c.Name));

        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();

            Execute(connection, $"CREATE TABLE Quests ({string.Join(", ", questColumns.Select(c => c.Definition))})");
            Execute(connection, $"CREATE TABLE Items ({string.Join(", ", itemColumns.Select(c => c.Definition))})");
            Execute(connection,
                "CREATE TABLE QuestRequirements (Id TEXT PRIMARY KEY, QuestId TEXT NOT NULL, RequiredQuestId TEXT NOT NULL, "
                + "RequirementType TEXT NOT NULL, GroupId INTEGER NOT NULL DEFAULT 0)");
            Execute(connection,
                "CREATE TABLE QuestObjectives (Id TEXT PRIMARY KEY, QuestId TEXT NOT NULL, SortOrder INTEGER NOT NULL, "
                + "Description TEXT NOT NULL)");
            Execute(connection,
                "CREATE TABLE HideoutItemRequirements (Id TEXT PRIMARY KEY, StationId TEXT NOT NULL, Level INTEGER NOT NULL, "
                + "ItemId TEXT NOT NULL, Count INTEGER NOT NULL)");

            if (withTraderRequirements)
            {
                Execute(connection,
                    "CREATE TABLE QuestTraderRequirements (Id TEXT PRIMARY KEY, QuestId TEXT NOT NULL, TraderId TEXT NOT NULL, "
                    + "TraderName TEXT NOT NULL, RequiredLevel INTEGER NOT NULL)");
            }

            foreach (var quest in quests ?? Array.Empty<(string, string, string?, bool, int?, string?)>())
            {
                var values = new List<(string Column, object Value)>
                {
                    ("Id", quest.Id),
                    ("BsgId", (object?)quest.BsgId ?? DBNull.Value),
                    ("Name", quest.Name),
                    ("KappaRequired", quest.Kappa ? 1 : 0),
                    ("MinLevel", (object?)quest.MinLevel ?? DBNull.Value),
                };
                if (withNormalizedName)
                    values.Add(("NormalizedName", (object?)quest.NormalizedName ?? DBNull.Value));

                Insert(connection, "Quests", values.Where(v => !omittedQuestColumns.Contains(v.Column)));
            }

            foreach (var (id, name, bsgId) in items ?? Array.Empty<(string, string, string?)>())
            {
                var values = new (string Column, object Value)[]
                {
                    ("Id", id),
                    ("BsgId", (object?)bsgId ?? DBNull.Value),
                    ("Name", name),
                };

                Insert(connection, "Items", values.Where(v => !omittedItemColumns.Contains(v.Column)));
            }

            var requirementIndex = 0;
            foreach (var (questId, requiredQuestId, type) in requirements ?? Array.Empty<(string, string, string)>())
            {
                using var cmd = new SqliteCommand(
                    "INSERT INTO QuestRequirements (Id, QuestId, RequiredQuestId, RequirementType) "
                    + "VALUES (@Id, @QuestId, @RequiredQuestId, @Type)", connection);
                cmd.Parameters.AddWithValue("@Id", $"req-{requirementIndex++}");
                cmd.Parameters.AddWithValue("@QuestId", questId);
                cmd.Parameters.AddWithValue("@RequiredQuestId", requiredQuestId);
                cmd.Parameters.AddWithValue("@Type", type);
                cmd.ExecuteNonQuery();
            }

            var objectiveIndex = 0;
            foreach (var (questId, sortOrder, description) in objectives ?? Array.Empty<(string, int, string)>())
            {
                using var cmd = new SqliteCommand(
                    "INSERT INTO QuestObjectives (Id, QuestId, SortOrder, Description) VALUES (@Id, @QuestId, @SortOrder, @Description)",
                    connection);
                cmd.Parameters.AddWithValue("@Id", $"obj-{objectiveIndex++}");
                cmd.Parameters.AddWithValue("@QuestId", questId);
                cmd.Parameters.AddWithValue("@SortOrder", sortOrder);
                cmd.Parameters.AddWithValue("@Description", description);
                cmd.ExecuteNonQuery();
            }

            var gateIndex = 0;
            foreach (var (questId, traderName, level) in traderGates ?? Array.Empty<(string, string, int)>())
            {
                using var cmd = new SqliteCommand(
                    "INSERT INTO QuestTraderRequirements (Id, QuestId, TraderId, TraderName, RequiredLevel) "
                    + "VALUES (@Id, @QuestId, @TraderId, @TraderName, @Level)", connection);
                cmd.Parameters.AddWithValue("@Id", $"gate-{gateIndex++}");
                cmd.Parameters.AddWithValue("@QuestId", questId);
                cmd.Parameters.AddWithValue("@TraderId", "trader-" + traderName);
                cmd.Parameters.AddWithValue("@TraderName", traderName);
                cmd.Parameters.AddWithValue("@Level", level);
                cmd.ExecuteNonQuery();
            }

            var hideoutIndex = 0;
            foreach (var (stationId, level, itemId, count) in hideoutRequirements ?? Array.Empty<(string, int, string, int)>())
            {
                using var cmd = new SqliteCommand(
                    "INSERT INTO HideoutItemRequirements (Id, StationId, Level, ItemId, Count) "
                    + "VALUES (@Id, @StationId, @Level, @ItemId, @Count)", connection);
                cmd.Parameters.AddWithValue("@Id", $"hir-{hideoutIndex++}");
                cmd.Parameters.AddWithValue("@StationId", stationId);
                cmd.Parameters.AddWithValue("@Level", level);
                cmd.Parameters.AddWithValue("@ItemId", itemId);
                cmd.Parameters.AddWithValue("@Count", count);
                cmd.ExecuteNonQuery();
            }
        }

        SqliteConnection.ClearAllPools();
        return path;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = new SqliteCommand(sql, connection);
        cmd.ExecuteNonQuery();
    }

    private static void Insert(
        SqliteConnection connection, string table, IEnumerable<(string Column, object Value)> values)
    {
        var pairs = values.ToList();
        var columns = string.Join(", ", pairs.Select(p => p.Column));
        var parameters = string.Join(", ", pairs.Select(p => "@" + p.Column));

        using var cmd = new SqliteCommand($"INSERT INTO {table} ({columns}) VALUES ({parameters})", connection);
        foreach (var (column, value) in pairs)
            cmd.Parameters.AddWithValue("@" + column, value);
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    #endregion
}
