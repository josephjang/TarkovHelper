using System.IO;
using System.Text.RegularExpressions;

namespace TarkovHelper.Tests;

/// <summary>
/// Guards the docs/decisions format: the four invariants named in the Risks section
/// of feature-decision-docs-process.md, plus the shape rules of the change-proposal
/// form adopted on 2026-09-13 (2026-09-13-adopt-change-proposal.md, R4). The format
/// is meant to be enforced by structure, not discipline, and these checks are the
/// structure. All run offline against the working tree (same repo-root walk as
/// UpdateXmlTests); the shape rules themselves live in ChangeProposalFormat, where
/// ChangeProposalFormatTests proves them on fixture text.
///
/// Scope rules: dated files (YYYY-MM-DD-slug...) and the three templates are the
/// change-proposal form and are held to its title, section, and pairing rules;
/// documents written before the adoption (feature-*, fix-*, *.spec.md) keep the
/// earlier decision-doc format and are held only to the original invariants; the
/// eleven documents flattened from the old active/ folder keep the legacy template
/// format and are exempted from the kept-current-field check by a closed allowlist
/// (the set can never grow, active/ is gone); archive/ is frozen history and out of
/// scope entirely; feature-decision-docs-process.spec.md is excluded from the
/// path-resolution check because it records the removed active/ paths and the
/// deleted template by design (mirroring verification checks 4 and 5 in that spec).
/// </summary>
public sealed class DecisionDocsTests
{
    /// <summary>
    /// Documents flattened from the old active/ folder in the decision-docs-process
    /// change. They keep the legacy template format (Status/Updated/Owner fields,
    /// checkboxes, Archive Info stubs) deliberately; only new-format documents are
    /// held to the no-kept-current-field invariant.
    /// </summary>
    private static readonly string[] LegacyFlattenedDocs =
    {
        "feature-fork-release-process.md",
        "feature-fork-release-process.ko.md",
        "feature-hideout-localized-sort.md",
        "feature-hideout-localized-sort.ko.md",
        "feature-persist-map-view-state.md",
        "feature-persist-map-view-state.ko.md",
        "feature-quest-unlock-sort.md",
        "feature-quest-unlock-sort.ko.md",
        "fix-quest-name-localization.md",
        "fix-quest-name-localization.ko.md",
        "fix-userdata-init-deadlock.md",
    };

    /// <summary>
    /// Records the removed active/ paths and the deleted feature-template.md in its
    /// own path-reference table and verification commands, so path tokens inside it
    /// intentionally do not resolve.
    /// </summary>
    private const string PathCheckExemptSpec = "feature-decision-docs-process.spec.md";

    private static string DecisionsDir() => Path.Combine(TestRepo.Root(), "docs", "decisions");

    private static string TemplatesDir() => Path.Combine(DecisionsDir(), "templates");

    private static IEnumerable<string> FlatDocs() =>
        Directory.EnumerateFiles(DecisionsDir(), "*.md", SearchOption.TopDirectoryOnly);

    /// <summary>
    /// New-format decision docs: everything flat in docs/decisions/ plus the
    /// templates, minus the README and the legacy allowlist. archive/ is excluded
    /// (frozen history). "New format" here means "not the pre-2026-07 template", so
    /// it covers both the decision-doc form and the change-proposal form.
    /// </summary>
    private static IEnumerable<string> NewFormatDocs()
    {
        var templates = Directory.EnumerateFiles(TemplatesDir(), "*.md", SearchOption.TopDirectoryOnly);

        return FlatDocs().Concat(templates).Where(path =>
        {
            var name = Path.GetFileName(path);
            return !string.Equals(name, "README.md", StringComparison.OrdinalIgnoreCase)
                   && !LegacyFlattenedDocs.Contains(name, StringComparer.OrdinalIgnoreCase);
        });
    }

    /// <summary>Flat documents whose name starts with a date: the change-proposal form.</summary>
    private static IEnumerable<string> DatedDocs() =>
        FlatDocs().Where(path => ChangeProposalFormat.IsDated(Path.GetFileName(path)));

    /// <summary>
    /// Every document that has a form to be checked against: the dated documents by
    /// their suffix, and the templates by their file name. A dated document whose
    /// name is not one of the three shapes has no form and is reported by
    /// <see cref="Dated_docs_take_one_of_the_three_names"/> instead.
    /// </summary>
    private static IEnumerable<(string Path, ChangeProposalFormat.Form Form)> FormedDocs()
    {
        foreach (var path in DatedDocs())
        {
            var form = ChangeProposalFormat.FormOf(Path.GetFileName(path));
            if (form != null)
            {
                yield return (path, form.Value);
            }
        }

        foreach (var name in ChangeProposalFormat.TemplateFileNames)
        {
            var path = Path.Combine(TemplatesDir(), name);
            if (File.Exists(path))
            {
                yield return (path, ChangeProposalFormat.FormOfTemplate(name)!.Value);
            }
        }
    }

    [Fact]
    public void New_format_docs_carry_no_kept_current_field()
    {
        // Field lines and structures of the deleted feature-template.md. Anchored to
        // line starts so prose that merely *mentions* the tokens (the PRD's Risks
        // section lists them in backticks) can't false-positive.
        var forbidden = new Regex(
            @"^\s*-\s+\*\*(Status|Updated|Owner|Related Agents)\*\*\s*:" +
            @"|^##\s+Progress Log\b" +
            @"|^\s*-\s+\[ \]",
            RegexOptions.Multiline);

        var violations = new List<string>();
        foreach (var path in NewFormatDocs())
        {
            foreach (Match match in forbidden.Matches(File.ReadAllText(path)))
            {
                violations.Add($"{Path.GetFileName(path)}: \"{match.Value.Trim()}\"");
            }
        }

        Assert.True(violations.Count == 0,
            "New-format decision docs must not reintroduce kept-current fields, a Progress Log, "
            + "or unticked checkboxes:\n" + string.Join("\n", violations));
    }

    /// <summary>
    /// The pairing rule of the pre-2026-09-13 decision-doc form, kept for the
    /// documents written in it: a spec never stood alone here. The change-proposal
    /// form's pairing (both ways, with links) is
    /// <see cref="Every_split_pair_is_complete_and_links_both_ways"/>.
    /// </summary>
    [Fact]
    public void Every_spec_has_its_sibling_prd()
    {
        var missing = new List<string>();
        foreach (var path in Directory.EnumerateFiles(DecisionsDir(), "*.spec.md", SearchOption.TopDirectoryOnly))
        {
            var sibling = path.Substring(0, path.Length - ".spec.md".Length) + ".md";
            if (!File.Exists(sibling))
            {
                missing.Add($"{Path.GetFileName(path)} has no {Path.GetFileName(sibling)} beside it");
            }
        }

        Assert.True(missing.Count == 0,
            "Every name.spec.md pairs with a name.md by filename:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void Every_korean_twin_has_its_english_original()
    {
        var missing = new List<string>();
        foreach (var path in Directory.EnumerateFiles(DecisionsDir(), "*.ko.md", SearchOption.AllDirectories))
        {
            var original = path.Substring(0, path.Length - ".ko.md".Length) + ".md";
            if (!File.Exists(original))
            {
                missing.Add($"{Path.GetFileName(path)} has no English original in its folder");
            }
        }

        Assert.True(missing.Count == 0,
            "Every .ko.md twin pairs 1:1 with its English original:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void Templates_exist_for_each_form()
    {
        var missing = ChangeProposalFormat.TemplateFileNames
            .Where(name => !File.Exists(Path.Combine(TemplatesDir(), name)))
            .ToList();

        Assert.True(missing.Count == 0,
            "docs/decisions/templates/ holds one template per change-proposal form:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void Dated_docs_take_one_of_the_three_names()
    {
        var unrecognized = DatedDocs()
            .Select(Path.GetFileName)
            .Where(name => ChangeProposalFormat.FormOf(name!) == null)
            .ToList();

        Assert.True(unrecognized.Count == 0,
            "A dated document is YYYY-MM-DD-<slug>.md, .requirements.md, or .design.md, with a "
            + "lower-case kebab-case slug (new documents are English only, so there is no .ko.md "
            + "shape):\n" + string.Join("\n", unrecognized));
    }

    [Fact]
    public void Dated_docs_and_templates_follow_their_forms_layout()
    {
        var violations = new List<string>();
        foreach (var (path, form) in FormedDocs())
        {
            foreach (var violation in ChangeProposalFormat.ShapeViolations(form, File.ReadAllText(path)))
            {
                violations.Add($"{Path.GetFileName(path)}: {violation}");
            }
        }

        Assert.True(violations.Count == 0,
            "A change proposal carries its form's title prefix and its fixed top-level sections, "
            + "in the template's order, with the required ones present:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void Every_split_pair_is_complete_and_links_both_ways()
    {
        var problems = new List<string>();
        foreach (var path in DatedDocs())
        {
            var name = Path.GetFileName(path);
            var counterpart = ChangeProposalFormat.CounterpartOf(name);
            if (counterpart == null)
            {
                continue;
            }

            if (!File.Exists(Path.Combine(DecisionsDir(), counterpart)))
            {
                problems.Add($"{name} has no {counterpart} beside it");
                continue;
            }

            if (!ChangeProposalFormat.LinksCounterpartBelowTitle(File.ReadAllText(path), counterpart))
            {
                problems.Add($"{name} does not link {counterpart} between its title and its first section");
            }
        }

        Assert.True(problems.Count == 0,
            "A Split proposal is its .requirements.md and .design.md together, each linking the other "
            + "below its title; neither alone is a proposal:\n" + string.Join("\n", problems));
    }

    [Fact]
    public void Every_referenced_decision_doc_path_resolves()
    {
        var root = TestRepo.Root();
        // Tokens carrying the folder's pre-rename name in frozen documents are
        // deliberately not scanned: they were true when written, and frozen
        // documents are not edited.
        var token = new Regex(@"docs/decisions/[A-Za-z0-9_/.\-]+\.md");
        // Directories that hold stale full copies of the repo or build output, not
        // sources of truth for path references.
        var skippedDirs = new[]
        {
            ".git", "bin", "obj", "packages", "TestResults", "node_modules", ".vs",
            Path.Combine(".claude", "worktrees"),
        };
        var extensions = new[]
        {
            ".md", ".cs", ".csproj", ".xaml", ".yml", ".yaml", ".json", ".ps1", ".xml", ".txt",
        };

        var broken = new List<string>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path);
            if (skippedDirs.Any(dir =>
                    relative.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || relative.Contains(Path.DirectorySeparatorChar + dir + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            if (!extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            if (string.Equals(Path.GetFileName(path), PathCheckExemptSpec, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (Match match in token.Matches(File.ReadAllText(path)))
            {
                var referenced = Path.Combine(root, match.Value.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(referenced))
                {
                    broken.Add($"{relative}: {match.Value}");
                }
            }
        }

        Assert.True(broken.Count == 0,
            "Every docs/decisions path written in a tracked file must resolve:\n" + string.Join("\n", broken));
    }
}
