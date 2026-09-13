using System.Text.RegularExpressions;

namespace TarkovHelper.Tests;

/// <summary>
/// The shape rules for change proposals in the form adopted on 2026-09-13
/// (2026-09-13-adopt-change-proposal.md, R3 and R4): which dated file names exist,
/// which title prefix each carries, which top-level sections each form has and in
/// which order, and how a Split pair names and links its counterpart. Pure
/// functions over a file name and its text, so DecisionDocsTests can run them over
/// the working tree and ChangeProposalFormatTests can prove they reject bad input.
/// The three templates under docs/decisions/templates/ are the authoritative
/// layouts; the section lists here mirror them. Public, unlike TestRepo, because
/// xunit theories take <see cref="Form"/> as a parameter and a public test method
/// cannot expose an internal type.
/// </summary>
public static class ChangeProposalFormat
{
    public enum Form
    {
        /// <summary>One document, <c>YYYY-MM-DD-slug.md</c>, titled "Change Proposal: ...".</summary>
        Unified,

        /// <summary>Product half of a Split proposal, <c>.requirements.md</c>, titled "Product Requirements: ...".</summary>
        ProductRequirements,

        /// <summary>Technical half of a Split proposal, <c>.design.md</c>, titled "Technical Design: ...".</summary>
        TechnicalDesign,
    }

    private const string RequirementsSuffix = ".requirements.md";
    private const string DesignSuffix = ".design.md";

    /// <summary>Any file whose name starts with a date belongs to the new form.</summary>
    private static readonly Regex DatedPrefix = new(@"^\d{4}-\d{2}-\d{2}-", RegexOptions.CultureInvariant);

    /// <summary>
    /// YYYY-MM-DD-slug with one of the three suffixes. The slug is lower-case
    /// kebab-case; there is no .ko.md shape because new documents are English only.
    /// </summary>
    private static readonly Regex DatedName = new(
        @"^\d{4}-\d{2}-\d{2}-[a-z0-9]+(?:-[a-z0-9]+)*(?<suffix>\.requirements|\.design)?\.md$",
        RegexOptions.CultureInvariant);

    /// <summary>A top-level markdown heading; anything deeper is a subsection and free-form.</summary>
    private static readonly Regex SectionHeading = new(@"^##\s+(?<name>.+?)\s*$", RegexOptions.CultureInvariant);

    private static readonly Regex TitleLine = new(@"^#\s+(?<title>.+?)\s*$", RegexOptions.CultureInvariant);

    /// <summary>HTML comments are instructions to the author (the templates are full of them), not content.</summary>
    private static readonly Regex HtmlComment = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    public static bool IsDated(string fileName) => DatedPrefix.IsMatch(fileName);

    /// <summary>
    /// Classifies a dated file name, or returns null when the name is dated but is
    /// not one of the three shapes (a .spec.md, a .ko.md twin, an upper-case slug).
    /// A file that is not dated at all is not the new form and also returns null;
    /// check <see cref="IsDated"/> first to tell the two apart.
    /// </summary>
    public static Form? FormOf(string fileName)
    {
        var match = DatedName.Match(fileName);
        if (!match.Success)
        {
            return null;
        }

        return match.Groups["suffix"].Value switch
        {
            ".requirements" => Form.ProductRequirements,
            ".design" => Form.TechnicalDesign,
            _ => Form.Unified,
        };
    }

    /// <summary>The template file that defines each form's layout, by its file name.</summary>
    public static Form? FormOfTemplate(string fileName) => fileName.ToLowerInvariant() switch
    {
        "change-proposal.md" => Form.Unified,
        "product-requirements.md" => Form.ProductRequirements,
        "technical-design.md" => Form.TechnicalDesign,
        _ => null,
    };

    /// <summary>The file names the three templates carry, one per form.</summary>
    public static IReadOnlyList<string> TemplateFileNames { get; } =
        new[] { "change-proposal.md", "product-requirements.md", "technical-design.md" };

    public static string TitlePrefix(Form form) => form switch
    {
        Form.Unified => "Change Proposal: ",
        Form.ProductRequirements => "Product Requirements: ",
        Form.TechnicalDesign => "Technical Design: ",
        _ => throw new ArgumentOutOfRangeException(nameof(form), form, "Unknown form"),
    };

    /// <summary>Top-level sections per form, in the order the templates fix them.</summary>
    public static IReadOnlyList<string> Sections(Form form) => form switch
    {
        Form.Unified => new[]
        {
            "Summary", "Problem", "Goals", "Non-Goals", "Requirements", "Decisions", "Risks",
        },
        Form.ProductRequirements => new[]
        {
            "Summary", "Problem", "Goals", "Non-Goals", "Requirements", "Product Decisions", "Risks",
        },
        Form.TechnicalDesign => new[]
        {
            "Summary", "Non-Goals", "Context", "Design", "Technical Decisions", "Open Questions",
            "Test Strategy", "Verification", "Risks & Migration",
        },
        _ => throw new ArgumentOutOfRangeException(nameof(form), form, "Unknown form"),
    };

    /// <summary>
    /// Sections a document of the form always has. For the two product-facing
    /// forms, the guide's "a change worth a proposal always has a problem, a goal,
    /// something it will not do, and a way to tell when it is done"; for a design,
    /// the design itself, the plan for checking it, and the record of what ran.
    /// </summary>
    public static IReadOnlyList<string> RequiredSections(Form form) => form switch
    {
        Form.Unified => new[] { "Problem", "Goals", "Non-Goals", "Requirements" },
        Form.ProductRequirements => new[] { "Problem", "Goals", "Non-Goals", "Requirements" },
        Form.TechnicalDesign => new[] { "Design", "Test Strategy", "Verification" },
        _ => throw new ArgumentOutOfRangeException(nameof(form), form, "Unknown form"),
    };

    /// <summary>
    /// The other half of a Split pair, or null for a Unified proposal (and for any
    /// name that is not one of the two Split suffixes).
    /// </summary>
    public static string? CounterpartOf(string fileName)
    {
        if (fileName.EndsWith(RequirementsSuffix, StringComparison.Ordinal))
        {
            return fileName.Substring(0, fileName.Length - RequirementsSuffix.Length) + DesignSuffix;
        }

        if (fileName.EndsWith(DesignSuffix, StringComparison.Ordinal))
        {
            return fileName.Substring(0, fileName.Length - DesignSuffix.Length) + RequirementsSuffix;
        }

        return null;
    }

    /// <summary>The first "# " line outside comments and code fences, or null when there is none.</summary>
    public static string? Title(string text) =>
        ContentLines(text).Select(line => TitleLine.Match(line))
            .FirstOrDefault(match => match.Success)?.Groups["title"].Value;

    /// <summary>The "## " headings outside comments and code fences, in document order.</summary>
    public static IReadOnlyList<string> Headings(string text) =>
        ContentLines(text).Select(line => SectionHeading.Match(line))
            .Where(match => match.Success)
            .Select(match => match.Groups["name"].Value)
            .ToList();

    /// <summary>
    /// Whether the document links its counterpart where the practice puts the
    /// link: below the title and above the first section. A mention further down
    /// (in Design, say) does not count, because the link's job is to identify the
    /// pair before the reader reads either half.
    /// </summary>
    public static bool LinksCounterpartBelowTitle(string text, string counterpartFileName)
    {
        var lines = ContentLines(text).ToList();
        var titleIndex = lines.FindIndex(line => TitleLine.IsMatch(line));
        if (titleIndex < 0)
        {
            return false;
        }

        var firstSection = lines.FindIndex(titleIndex + 1, line => SectionHeading.IsMatch(line));
        var end = firstSection < 0 ? lines.Count : firstSection;
        return lines.Skip(titleIndex + 1).Take(end - titleIndex - 1)
            .Any(line => line.Contains(counterpartFileName, StringComparison.Ordinal));
    }

    /// <summary>
    /// Everything wrong with a document's title and top-level sections for its
    /// form; empty when it conforms. Each entry is one sentence a failure message
    /// can print as is.
    /// </summary>
    public static IReadOnlyList<string> ShapeViolations(Form form, string text)
    {
        var violations = new List<string>();

        var prefix = TitlePrefix(form);
        var title = Title(text);
        if (title == null)
        {
            violations.Add($"has no title line (expected \"# {prefix}<name>\")");
        }
        else if (!title.StartsWith(prefix, StringComparison.Ordinal)
                 || title.Length == prefix.Length)
        {
            violations.Add($"title \"{title}\" does not read \"{prefix}<name>\"");
        }

        var allowed = Sections(form);
        var lastIndex = -1;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var heading in Headings(text))
        {
            var index = IndexOf(allowed, heading);
            if (index < 0)
            {
                violations.Add($"section \"{heading}\" is not one of the {form} sections ({string.Join(", ", allowed)})");
                continue;
            }

            if (!seen.Add(heading))
            {
                violations.Add($"section \"{heading}\" appears more than once");
                continue;
            }

            if (index < lastIndex)
            {
                violations.Add($"section \"{heading}\" is out of order (the {form} order is {string.Join(", ", allowed)})");
            }

            lastIndex = Math.Max(lastIndex, index);
        }

        foreach (var required in RequiredSections(form))
        {
            if (!seen.Contains(required))
            {
                violations.Add($"section \"{required}\" is missing (a {form} document always has it)");
            }
        }

        return violations;
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The document's lines with HTML comments and fenced code blocks removed, so a
    /// "## " inside a pasted example or an author instruction is not read as a
    /// section. Line structure is preserved for what remains.
    /// </summary>
    private static IEnumerable<string> ContentLines(string text)
    {
        var withoutComments = HtmlComment.Replace(text, string.Empty);
        var inFence = false;
        foreach (var rawLine in withoutComments.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (!inFence)
            {
                yield return line;
            }
        }
    }
}
