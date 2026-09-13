using System.IO;

namespace TarkovHelper.Tests;

/// <summary>
/// Proves the change-proposal shape rules in ChangeProposalFormat on fixture text:
/// that each rule rejects the input it exists to reject, and accepts a conforming
/// document. DecisionDocsTests applies the same rules to the working tree; a rule
/// that only ever ran against conforming files would have no evidence that it can
/// fail, which is why these exist.
/// </summary>
public sealed class ChangeProposalFormatTests
{
    private const string ConformingUnified =
        "# Change Proposal: Adopt something\n\n" +
        "## Summary\n\nShort.\n\n" +
        "## Problem\n\nWhat is wrong.\n\n" +
        "## Goals\n\n- X is possible.\n\n" +
        "## Non-Goals\n\n- Not Y.\n\n" +
        "## Requirements\n\n- R1: Z holds.\n\n" +
        "## Decisions\n\n- **D1: Chosen.** Rejected, because.\n\n" +
        "## Risks\n\n- Accepted.\n";

    private const string ConformingDesign =
        "# Technical Design: Note trash\n\n" +
        "Product Requirements: [Note trash](2026-09-12-note-trash.requirements.md)\n\n" +
        "## Summary\n\nShape.\n\n" +
        "## Context\n\nVerified at abc123.\n\n" +
        "## Design\n\nHow R1 is met.\n\n### Files touched\n\n- `A.cs`\n\n" +
        "## Technical Decisions\n\n- **TD1: Chosen.** Rejected, because.\n\n" +
        "## Test Strategy\n\n- R1: check; expected.\n\n" +
        "## Verification\n\n- Checked: R1 at abc123, passed.\n\n" +
        "## Risks & Migration\n\nNone.\n";

    [Theory]
    [InlineData("2026-09-13-adopt-change-proposal.md", ChangeProposalFormat.Form.Unified)]
    [InlineData("2026-09-12-note-trash.requirements.md", ChangeProposalFormat.Form.ProductRequirements)]
    [InlineData("2026-09-12-note-trash.design.md", ChangeProposalFormat.Form.TechnicalDesign)]
    [InlineData("2026-01-01-a1.md", ChangeProposalFormat.Form.Unified)]
    public void FormOf_recognizes_the_three_dated_shapes(string fileName, ChangeProposalFormat.Form expected)
    {
        Assert.True(ChangeProposalFormat.IsDated(fileName));
        Assert.Equal(expected, ChangeProposalFormat.FormOf(fileName));
    }

    [Theory]
    [InlineData("2026-09-13-adopt.spec.md")]      // the older suffix on a dated name
    [InlineData("2026-09-13-adopt.ko.md")]        // a Korean twin: new documents are English only
    [InlineData("2026-09-13-Adopt-Change.md")]    // slug is lower-case kebab-case
    [InlineData("2026-09-13-adopt_change.md")]    // underscores are not kebab-case
    [InlineData("2026-09-13-.md")]                // empty slug
    [InlineData("2026-09-13-adopt.requirements.ko.md")]
    public void FormOf_rejects_a_dated_name_of_any_other_shape(string fileName)
    {
        Assert.True(ChangeProposalFormat.IsDated(fileName));
        Assert.Null(ChangeProposalFormat.FormOf(fileName));
    }

    [Theory]
    [InlineData("feature-quest-loyalty-gating.md")]
    [InlineData("fix-profile-settings-race.spec.md")]
    [InlineData("README.md")]
    public void Undated_names_are_not_the_new_form(string fileName)
    {
        Assert.False(ChangeProposalFormat.IsDated(fileName));
        Assert.Null(ChangeProposalFormat.FormOf(fileName));
    }

    [Fact]
    public void FormOfTemplate_maps_the_three_template_names_and_nothing_else()
    {
        Assert.Equal(ChangeProposalFormat.Form.Unified, ChangeProposalFormat.FormOfTemplate("change-proposal.md"));
        Assert.Equal(ChangeProposalFormat.Form.ProductRequirements, ChangeProposalFormat.FormOfTemplate("product-requirements.md"));
        Assert.Equal(ChangeProposalFormat.Form.TechnicalDesign, ChangeProposalFormat.FormOfTemplate("technical-design.md"));
        Assert.Null(ChangeProposalFormat.FormOfTemplate("prd-template.md"));
        Assert.Null(ChangeProposalFormat.FormOfTemplate("spec-template.md"));
        Assert.Equal(3, ChangeProposalFormat.TemplateFileNames.Count);
        Assert.All(ChangeProposalFormat.TemplateFileNames, name => Assert.NotNull(ChangeProposalFormat.FormOfTemplate(name)));
    }

    [Theory]
    [InlineData("2026-09-12-note-trash.requirements.md", "2026-09-12-note-trash.design.md")]
    [InlineData("2026-09-12-note-trash.design.md", "2026-09-12-note-trash.requirements.md")]
    public void CounterpartOf_swaps_the_split_suffixes(string fileName, string expected)
    {
        Assert.Equal(expected, ChangeProposalFormat.CounterpartOf(fileName));
    }

    [Theory]
    [InlineData("2026-09-13-adopt-change-proposal.md")]
    [InlineData("feature-x.spec.md")]
    public void CounterpartOf_is_null_outside_a_split_pair(string fileName)
    {
        Assert.Null(ChangeProposalFormat.CounterpartOf(fileName));
    }

    [Fact]
    public void A_conforming_unified_proposal_has_no_violations()
    {
        Assert.Empty(ChangeProposalFormat.ShapeViolations(ChangeProposalFormat.Form.Unified, ConformingUnified));
    }

    [Fact]
    public void A_conforming_design_has_no_violations()
    {
        Assert.Empty(ChangeProposalFormat.ShapeViolations(ChangeProposalFormat.Form.TechnicalDesign, ConformingDesign));
    }

    [Fact]
    public void Optional_sections_may_be_deleted()
    {
        // The guide: a section with nothing to say is deleted, not filled. Summary,
        // Decisions and Risks go; the four required ones stay.
        var minimal =
            "# Change Proposal: Small fix\n\n" +
            "## Problem\n\nP.\n\n## Goals\n\n- G.\n\n## Non-Goals\n\n- N.\n\n## Requirements\n\n- R1: R.\n";

        Assert.Empty(ChangeProposalFormat.ShapeViolations(ChangeProposalFormat.Form.Unified, minimal));
    }

    [Theory]
    [InlineData(ChangeProposalFormat.Form.Unified, "# Adopt something", "Change Proposal: ")]
    [InlineData(ChangeProposalFormat.Form.Unified, "# Product Requirements: Adopt something", "Change Proposal: ")]
    [InlineData(ChangeProposalFormat.Form.ProductRequirements, "# Change Proposal: X", "Product Requirements: ")]
    [InlineData(ChangeProposalFormat.Form.TechnicalDesign, "# Technical Spec: X", "Technical Design: ")]
    [InlineData(ChangeProposalFormat.Form.TechnicalDesign, "# Technical Design: ", "Technical Design: ")]
    public void A_title_without_the_forms_prefix_and_a_name_is_a_violation(
        ChangeProposalFormat.Form form, string titleLine, string expectedPrefix)
    {
        var body = ConformingUnified.Substring(ConformingUnified.IndexOf('\n'));
        var text = titleLine + body;

        var violation = Assert.Single(
            ChangeProposalFormat.ShapeViolations(form, text).Where(v => v.StartsWith("title", StringComparison.Ordinal)));
        Assert.Contains(expectedPrefix, violation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_title_is_a_violation()
    {
        var text = ConformingUnified.Substring(ConformingUnified.IndexOf('\n'));

        Assert.Contains(ChangeProposalFormat.ShapeViolations(ChangeProposalFormat.Form.Unified, text),
            v => v.Contains("no title", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unknown_section_is_a_violation()
    {
        var text = ConformingUnified.Replace("## Decisions", "## Product Decisions");

        var violation = Assert.Single(ChangeProposalFormat.ShapeViolations(ChangeProposalFormat.Form.Unified, text));
        Assert.Contains("\"Product Decisions\" is not one of the Unified sections", violation);
    }

    [Fact]
    public void A_legacy_section_name_is_a_violation_for_the_new_form()
    {
        // The old spec template's headings do not carry over unchanged: the README
        // maps them, and the guard is what makes the mapping stick.
        var text = ConformingDesign.Replace("## Context", "## Current Behavior / Root Cause");

        var violation = Assert.Single(ChangeProposalFormat.ShapeViolations(ChangeProposalFormat.Form.TechnicalDesign, text));
        Assert.Contains("\"Current Behavior / Root Cause\" is not one of the TechnicalDesign sections", violation);
    }

    [Fact]
    public void An_out_of_order_section_is_a_violation()
    {
        var text = ConformingUnified
            .Replace("## Problem\n\nWhat is wrong.\n\n", string.Empty)
            .Replace("## Risks\n\n- Accepted.\n", "## Risks\n\n- Accepted.\n\n## Problem\n\nWhat is wrong.\n");

        var violation = Assert.Single(ChangeProposalFormat.ShapeViolations(ChangeProposalFormat.Form.Unified, text));
        Assert.Contains("\"Problem\" is out of order", violation);
    }

    [Fact]
    public void A_repeated_section_is_a_violation()
    {
        var text = ConformingUnified + "\n## Risks\n\n- Another.\n";

        var violation = Assert.Single(ChangeProposalFormat.ShapeViolations(ChangeProposalFormat.Form.Unified, text));
        Assert.Contains("\"Risks\" appears more than once", violation);
    }

    [Theory]
    [InlineData(ChangeProposalFormat.Form.Unified, "Non-Goals")]
    [InlineData(ChangeProposalFormat.Form.Unified, "Requirements")]
    [InlineData(ChangeProposalFormat.Form.ProductRequirements, "Problem")]
    public void A_missing_required_section_is_a_violation(ChangeProposalFormat.Form form, string section)
    {
        var text = ConformingUnified
            .Replace("# Change Proposal: ", "# " + ChangeProposalFormat.TitlePrefix(form))
            .Replace("## Decisions", form == ChangeProposalFormat.Form.Unified ? "## Decisions" : "## Product Decisions");
        var start = text.IndexOf("## " + section, StringComparison.Ordinal);
        var end = text.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        text = text.Remove(start, end - start + 1);

        var violation = Assert.Single(ChangeProposalFormat.ShapeViolations(form, text));
        Assert.Contains($"\"{section}\" is missing", violation);
    }

    [Theory]
    [InlineData("Design")]
    [InlineData("Test Strategy")]
    [InlineData("Verification")]
    public void A_design_without_its_plan_or_its_record_is_a_violation(string section)
    {
        var start = ConformingDesign.IndexOf("## " + section, StringComparison.Ordinal);
        var end = ConformingDesign.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        var text = ConformingDesign.Remove(start, end - start + 1);

        var violation = Assert.Single(ChangeProposalFormat.ShapeViolations(ChangeProposalFormat.Form.TechnicalDesign, text));
        Assert.Contains($"\"{section}\" is missing", violation);
    }

    [Fact]
    public void Headings_inside_code_fences_and_html_comments_are_not_sections()
    {
        var text = ConformingUnified
            .Replace("## Problem\n\nWhat is wrong.\n\n",
                "## Problem\n\n<!-- ## Not A Section -->\n\n```markdown\n## Also Not A Section\n```\n\nWhat is wrong.\n\n");

        Assert.Empty(ChangeProposalFormat.ShapeViolations(ChangeProposalFormat.Form.Unified, text));
        Assert.Equal(
            new[] { "Summary", "Problem", "Goals", "Non-Goals", "Requirements", "Decisions", "Risks" },
            ChangeProposalFormat.Headings(text));
    }

    [Fact]
    public void Subsections_are_free_form()
    {
        Assert.Empty(ChangeProposalFormat.ShapeViolations(ChangeProposalFormat.Form.TechnicalDesign, ConformingDesign));
        Assert.DoesNotContain("Files touched", ChangeProposalFormat.Headings(ConformingDesign));
    }

    [Fact]
    public void A_counterpart_link_counts_only_between_the_title_and_the_first_section()
    {
        Assert.True(ChangeProposalFormat.LinksCounterpartBelowTitle(ConformingDesign, "2026-09-12-note-trash.requirements.md"));

        var linkedTooLate = ConformingDesign
            .Replace("Product Requirements: [Note trash](2026-09-12-note-trash.requirements.md)\n\n", string.Empty)
            .Replace("## Context\n\n", "## Context\n\nSee 2026-09-12-note-trash.requirements.md.\n\n");
        Assert.False(ChangeProposalFormat.LinksCounterpartBelowTitle(linkedTooLate, "2026-09-12-note-trash.requirements.md"));

        Assert.False(ChangeProposalFormat.LinksCounterpartBelowTitle(ConformingDesign, "2026-09-12-other.requirements.md"));
        Assert.False(ChangeProposalFormat.LinksCounterpartBelowTitle("no title at all", "x.requirements.md"));
    }

    [Fact]
    public void The_shipped_templates_conform_to_their_own_forms()
    {
        // The templates are the authoritative layouts, so they must pass the rules
        // derived from them, placeholders included ("[Change name]" is a name).
        var templates = Path.Combine(TestRepo.Root(), "docs", "decisions", "templates");
        foreach (var name in ChangeProposalFormat.TemplateFileNames)
        {
            var form = ChangeProposalFormat.FormOfTemplate(name)!.Value;
            var violations = ChangeProposalFormat.ShapeViolations(form, File.ReadAllText(Path.Combine(templates, name)));
            Assert.True(violations.Count == 0, $"{name}: {string.Join("; ", violations)}");
        }
    }
}
