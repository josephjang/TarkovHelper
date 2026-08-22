using TarkovDBEditor.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// The two wiki parsers the 1.1 refresh added or widened.
/// <para>
/// The seasonal marker is the single exception to "a quest ships only when both sources have
/// it": the JSON API carries no record for the eighteen KORD BREACH quests in any game mode, so
/// this line is the only evidence they exist. Reading it too loosely imports pages that merely
/// mention a season; reading it too tightly makes the whole questline disappear in silence.
/// </para>
/// </summary>
public sealed class WikiQuestParsingTests
{
    #region Seasonal marker

    [Fact]
    public void Recognises_the_link_form_every_seasonal_page_uses_today()
    {
        var page = Requirements("* Must be playing in the [[Seasons#Season 1: KORD BREACH|Seasonal mode]].");

        Assert.True(WikiQuestService.ExtractIsSeasonal(page));
    }

    [Fact]
    public void Recognises_the_bare_link_form_an_earlier_census_recorded()
    {
        var page = Requirements("* Must be playing in the [[PvP Season]].");

        Assert.True(WikiQuestService.ExtractIsSeasonal(page));
    }

    [Fact]
    public void Recognises_a_nested_bullet()
    {
        var page = Requirements("** Must be playing in the [[Seasons#Season 1: KORD BREACH|Seasonal mode]].");

        Assert.True(WikiQuestService.ExtractIsSeasonal(page));
    }

    [Fact]
    public void An_ordinary_quest_is_not_seasonal()
    {
        Assert.False(WikiQuestService.ExtractIsSeasonal(Requirements("* Must be level 15 to start this quest.")));
    }

    [Fact]
    public void A_page_that_only_mentions_a_season_in_prose_is_not_imported_on_that_alone()
    {
        // Deliberately narrow: this quest has a game record like any other, and importing it
        // without one because of a sentence would be a guess.
        var page = Requirements("* Must be level 15 to start this quest.")
            + "\n==Notes==\nThis quest was added during the KORD BREACH seasonal mode.\n";

        Assert.False(WikiQuestService.ExtractIsSeasonal(page));
    }

    [Fact]
    public void The_loose_check_notices_a_marker_that_the_strict_one_missed()
    {
        // This pair is what the refresh guard compares: pages that talk about a seasonal mode
        // while none is recognised means the wiki's wording moved and the strict reader needs
        // updating, rather than eighteen quests quietly leaving the app.
        var moved = Requirements("* Must be playing in the current seasonal mode to start this quest.");

        Assert.False(WikiQuestService.ExtractIsSeasonal(moved));
        Assert.True(WikiQuestService.MentionsSeasonalMode(moved));
    }

    [Fact]
    public void The_loose_check_is_confined_to_the_requirements_section()
    {
        var page = Requirements("* Must be level 15 to start this quest.")
            + "\n==Notes==\nAvailable in seasonal mode as well.\n";

        Assert.False(WikiQuestService.MentionsSeasonalMode(page));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{{Infobox quest\n|given by = [[Prapor]]\n}}")]
    public void A_page_with_no_requirements_section_is_neither(string page)
    {
        Assert.False(WikiQuestService.ExtractIsSeasonal(page));
        Assert.False(WikiQuestService.MentionsSeasonalMode(page));
    }

    #endregion

    #region Objective map names

    [Theory]
    [InlineData("The Labyrinth")]
    [InlineData("Terminal")]
    [InlineData("Icebreaker")]
    public void Recognises_the_maps_patch_1_1_added(string mapName)
    {
        var objectives = WikiQuestService.ExtractObjectives(
            $"==Objectives==\n* Eliminate 10 Scavs on [[{mapName}]]\n");

        Assert.Equal(mapName, Assert.Single(objectives).MapName);
    }

    [Fact]
    public void The_Labyrinth_does_not_get_claimed_by_the_shorter_Lab()
    {
        // "Lab" and "The Lab" are both in the list and both are prefixes of "The Labyrinth".
        var objectives = WikiQuestService.ExtractObjectives(
            "==Objectives==\n* Survive and extract from [[The Labyrinth]]\n");

        Assert.Equal("The Labyrinth", Assert.Single(objectives).MapName);
    }

    [Fact]
    public void The_Lab_is_still_normalized_to_Lab()
    {
        var objectives = WikiQuestService.ExtractObjectives(
            "==Objectives==\n* Survive and extract from [[The Lab]]\n");

        Assert.Equal("Lab", Assert.Single(objectives).MapName);
    }

    [Fact]
    public void Streets_of_Tarkov_is_recognised_in_full()
    {
        var objectives = WikiQuestService.ExtractObjectives(
            "==Objectives==\n* Eliminate 10 Scavs on [[Streets of Tarkov]]\n");

        Assert.Equal("Streets of Tarkov", Assert.Single(objectives).MapName);
    }

    #endregion

    private static string Requirements(params string[] bullets) =>
        "{{Infobox quest\n|given by = [[Prapor]]\n}}\n==Requirements==\n" + string.Join("\n", bullets) + "\n==Objectives==\n";
}
