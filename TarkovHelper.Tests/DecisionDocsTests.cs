using System.IO;
using System.Text.RegularExpressions;

namespace TarkovHelper.Tests;

/// <summary>
/// Guards the four decision-doc invariants named in the Risks section of
/// feature-decision-docs-process.md: the docs/decisions format is meant to be
/// enforced by structure, not discipline, and these checks are the structure. All
/// four run offline against the working tree (same repo-root walk as UpdateXmlTests).
///
/// Scope rules: the eleven documents flattened from the old active/ folder keep
/// their legacy format and are exempted by a closed allowlist (the set can never
/// grow, active/ is gone); archive/ is frozen history and out of scope entirely;
/// feature-decision-docs-process.spec.md is excluded from the path-resolution check
/// because it records the removed active/ paths and the deleted template by design
/// (mirroring verification checks 4–5 in that spec).
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

    /// <summary>
    /// New-format decision docs: everything flat in docs/decisions/ plus the two
    /// templates, minus the README and the legacy allowlist. archive/ is excluded
    /// (frozen history).
    /// </summary>
    private static IEnumerable<string> NewFormatDocs()
    {
        var decisions = DecisionsDir();
        var flat = Directory.EnumerateFiles(decisions, "*.md", SearchOption.TopDirectoryOnly);
        var templates = Directory.EnumerateFiles(
            Path.Combine(decisions, "templates"), "*.md", SearchOption.TopDirectoryOnly);

        return flat.Concat(templates).Where(path =>
        {
            var name = Path.GetFileName(path);
            return !string.Equals(name, "README.md", StringComparison.OrdinalIgnoreCase)
                   && !LegacyFlattenedDocs.Contains(name, StringComparer.OrdinalIgnoreCase);
        });
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
