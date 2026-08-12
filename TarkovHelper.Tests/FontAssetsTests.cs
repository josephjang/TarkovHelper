using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using TarkovHelper.Services;
// The test project also references WinForms (for TarkovDBEditor); disambiguate.
using FontFamily = System.Windows.Media.FontFamily;

namespace TarkovHelper.Tests;

/// <summary>
/// Guards the shipped font assets named in feature-eft-font-stack.spec.md:
/// every embedded file must open as a real GlyphTypeface (CFF/TTF parse guard),
/// group into exactly the three expected WWS families with true Regular + Bold cuts
/// (no synthesized bold), cover the scripts each face is responsible for, stay in
/// sync with the csproj resource list, and leave no Maplestory reference behind.
///
/// Fonts load by file URI from the source tree, not pack URI: pack://application:
/// needs WPF application bootstrapping that is brittle under the xunit host, and
/// the on-disk files are byte-identical to what gets embedded (the embedding itself
/// is guarded separately by Fonts_are_embedded_in_the_app_assembly).
/// </summary>
public sealed class FontAssetsTests
{
    /// <summary>
    /// The three families the shipped files must group into, with the exact
    /// weights the app's chains rely on.
    /// </summary>
    private static readonly string[] ExpectedFamilies = { "Bender", "Play", "Noto Sans CJK KR" };

    private static string FontsDir() => Path.Combine(TestRepo.Root(), "TarkovHelper", "Fonts");

    private static IReadOnlyList<string> ShippedFontFiles() =>
        Directory.EnumerateFiles(FontsDir())
            .Where(path => Path.GetExtension(path) is ".ttf" or ".otf")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

    [Fact]
    public void Every_shipped_font_opens_as_a_glyph_typeface()
    {
        var files = ShippedFontFiles();
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            // GlyphTypeface construction parses the full font through DirectWrite;
            // a corrupt download or an outline format WPF can't render throws here.
            var glyphTypeface = new GlyphTypeface(new Uri(file));
            Assert.True(glyphTypeface.GlyphCount > 0, $"{Path.GetFileName(file)} has no glyphs");
        }
    }

    [Fact]
    public void Shipped_fonts_group_into_exactly_the_expected_families()
    {
        // Trailing separator: Fonts.GetFontFamilies treats a bare directory path
        // as a file location and silently returns nothing.
        var families = Fonts.GetFontFamilies(FontsDir() + Path.DirectorySeparatorChar)
            .Select(family => family.Source[(family.Source.IndexOf('#') + 1)..])
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            ExpectedFamilies.OrderBy(name => name, StringComparer.Ordinal),
            families);
    }

    [Theory]
    [InlineData("Bender")]
    [InlineData("Play")]
    [InlineData("Noto Sans CJK KR")]
    public void Bold_and_normal_typefaces_resolve_without_simulation(string familyName)
    {
        var baseUri = new Uri(FontsDir() + Path.DirectorySeparatorChar);
        var family = new FontFamily(baseUri, "./#" + familyName);

        foreach (var weight in new[] { FontWeights.Normal, FontWeights.Bold })
        {
            var typeface = new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal);
            Assert.True(typeface.TryGetGlyphTypeface(out var glyphTypeface),
                $"{familyName} {weight} did not resolve to a glyph typeface");
            // StyleSimulations.None proves this is a true cut: a missing Bold
            // face would resolve with BoldSimulation (the old Maplestory faux bold).
            Assert.Equal(StyleSimulations.None, glyphTypeface.StyleSimulations);
            Assert.Equal(weight, glyphTypeface.Weight);
        }
    }

    [Theory]
    // Bender owns Latin, digits, and Cyrillic in every chain, the same
    // scripts it serves in the game's own UI.
    [InlineData("Bender-Regular.otf", "ABCXYZ0189АяЁë№")]
    [InlineData("Bender-Bold.otf", "ABCXYZ0189АяЁë№")]
    // Play backstops the same scripts for any glyph Bender lacks.
    [InlineData("Play-Regular.ttf", "ABCXYZ0189АяЁë")]
    [InlineData("Play-Bold.ttf", "ABCXYZ0189АяЁë")]
    // Noto is the bundled CJK face: Hangul for KO plus kana/kanji as the
    // last-resort JA fallback (調査 appears in quest text). The six symbols are the
    // app's own status/direction glyphs (QuestListPage, MapQuestMarkerManager,
    // MapPage): neither Bender nor Play covers them, so in the EN/KO chain Noto is
    // the only named family that can render them, and a subset build (the ~10 MB option
    // the PRD records as offered and declined) would leave them to blind fallback.
    [InlineData("NotoSansCJKkr-Regular.otf", "가한調査あア✓○▲▼↑↓")]
    [InlineData("NotoSansCJKkr-Bold.otf", "가한調査あア✓○▲▼↑↓")]
    public void Shipped_fonts_cover_their_scripts(string fileName, string sampleChars)
    {
        var glyphTypeface = new GlyphTypeface(new Uri(Path.Combine(FontsDir(), fileName)));
        var missing = sampleChars
            .Where(ch => !glyphTypeface.CharacterToGlyphMap.ContainsKey(ch))
            .ToList();

        Assert.True(missing.Count == 0,
            $"{fileName} is missing glyphs for: {string.Join(" ", missing)}");
    }

    [Fact]
    public void Chain_fragments_match_the_embedded_families_exactly()
    {
        var chainFamilies = Enum.GetValues<AppLanguage>()
            .Select(FontStacks.ForLanguage)
            .SelectMany(chain => chain.Split(','))
            .Select(token => token.Trim())
            .Where(token => token.StartsWith("./Fonts/#", StringComparison.Ordinal))
            .Select(token => token["./Fonts/#".Length..])
            .Distinct()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            ExpectedFamilies.OrderBy(name => name, StringComparer.Ordinal),
            chainFamilies);
    }

    [Fact]
    public void App_xaml_appfont_comes_from_fontstacks_default()
    {
        // The compiled default must reference FontStacks.DefaultFamily via x:Static,
        // the single source of truth for the chain string. A literal chain here would
        // reintroduce the two-copies drift this reference removed.
        var appXaml = File.ReadAllText(Path.Combine(TestRepo.Root(), "TarkovHelper", "App.xaml"));

        Assert.Matches(
            "<x:Static x:Key=\"AppFont\" Member=\"services:FontStacks.DefaultFamily\" */>",
            appXaml);

        // And the default the XAML picks up is the EN/KO chain with the pack base URI.
        Assert.Equal(FontStacks.ForLanguage(AppLanguage.EN), FontStacks.DefaultFamily.Source);
        Assert.Equal(FontStacks.PackBaseUri, FontStacks.DefaultFamily.BaseUri);
    }

    [Fact]
    public void Every_dynamicresource_key_in_app_xaml_is_declared()
    {
        // The Maplestory -> AppFont change moved ~10 FontFamily setters from
        // StaticResource (load-time key-existence check, fails fast) to
        // DynamicResource (silent fallback on a missing key). This re-establishes
        // the fail-fast guarantee: every {DynamicResource Key} referenced in
        // App.xaml must be declared there.
        var appXaml = File.ReadAllText(Path.Combine(TestRepo.Root(), "TarkovHelper", "App.xaml"));

        var referencedKeys = Regex.Matches(appXaml, @"\{DynamicResource (?<key>\w+)\}")
            .Select(m => m.Groups["key"].Value)
            .Distinct()
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();
        Assert.NotEmpty(referencedKeys);

        var undeclared = referencedKeys
            .Where(key => !appXaml.Contains($"x:Key=\"{key}\"", StringComparison.Ordinal))
            .ToList();

        Assert.True(undeclared.Count == 0,
            "DynamicResource keys referenced in App.xaml but never declared there "
            + "(a typo would silently fall back instead of failing at load): "
            + string.Join(", ", undeclared));
    }

    [Fact]
    public void Fonts_are_embedded_in_the_app_assembly()
    {
        // The file-URI tests above prove the on-disk faces are valid; this proves the
        // csproj actually embeds them. Dropping a <Resource> entry (or renaming the
        // Fonts folder) would otherwise ship an app that silently renders in system
        // fonts, and WPF never throws for an unresolvable ./Fonts/#Family token.
        using var stream = typeof(App).Assembly.GetManifestResourceStream("TarkovHelper.g.resources");
        Assert.NotNull(stream);

        using var reader = new System.Resources.ResourceReader(stream!);
        var entries = reader.Cast<System.Collections.DictionaryEntry>()
            .Select(entry => (string)entry.Key!)
            .Where(key => key.StartsWith("fonts/", StringComparison.Ordinal))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        var expected = ShippedFontFiles()
            .Select(path => "fonts/" + Path.GetFileName(path)!.ToLowerInvariant())
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expected, entries);
    }

    [Theory]
    // The chain-leading Latin faces must NOT own CJK scripts: a Bender/Play build
    // that gained Hangul or kana would capture those scripts ahead of the CJK faces
    // (exactly how the old Maplestory face captured Hangul) while every ordering
    // assertion stays green.
    [InlineData("Bender-Regular.otf", "가한調あア")]
    [InlineData("Bender-Bold.otf", "가한調あア")]
    [InlineData("Play-Regular.ttf", "가한調あア")]
    [InlineData("Play-Bold.ttf", "가한調あア")]
    public void Latin_faces_do_not_capture_cjk_scripts(string fileName, string forbiddenChars)
    {
        var glyphTypeface = new GlyphTypeface(new Uri(Path.Combine(FontsDir(), fileName)));
        var captured = forbiddenChars
            .Where(ch => glyphTypeface.CharacterToGlyphMap.ContainsKey(ch))
            .ToList();

        Assert.True(captured.Count == 0,
            $"{fileName} unexpectedly covers CJK glyphs (would capture them ahead of the CJK faces): "
            + string.Join(" ", captured));
    }

    [Fact]
    public void Csproj_resource_entries_match_the_fonts_directory()
    {
        var csprojPath = Path.Combine(TestRepo.Root(), "TarkovHelper", "TarkovHelper.csproj");
        var csproj = File.ReadAllText(csprojPath);

        var declared = Regex.Matches(csproj, "<Resource Include=\"Fonts\\\\(?<file>[^\"]+)\" */>")
            .Select(m => m.Groups["file"].Value)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var onDisk = ShippedFontFiles()
            .Select(path => Path.GetFileName(path)!)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Equal(onDisk, declared);

        // The license texts ride through publish via the Fonts\*.txt None entry.
        Assert.Contains("<None Update=\"Fonts\\*.txt\">", csproj);
        Assert.True(File.Exists(Path.Combine(FontsDir(), "LICENSE-Bender.txt")),
            "LICENSE-Bender.txt (attribution/provenance) must ship next to the Bender faces");
        Assert.True(File.Exists(Path.Combine(FontsDir(), "LICENSE-Play.txt")),
            "LICENSE-Play.txt must ship next to the Play faces");
        Assert.True(File.Exists(Path.Combine(FontsDir(), "LICENSE-NotoSansCJK.txt")),
            "LICENSE-NotoSansCJK.txt must ship next to the Noto faces");
    }

    [Fact]
    public void No_maplestory_reference_survives_in_the_app_project()
    {
        var projectRoot = Path.Combine(TestRepo.Root(), "TarkovHelper");
        var extensions = new[] { ".cs", ".xaml", ".csproj", ".md", ".txt", ".json", ".xml", ".ps1" };
        var skippedDirs = new[] { "bin", "obj" };

        var survivors = new List<string>();
        foreach (var path in Directory.EnumerateFiles(projectRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(projectRoot, path);
            if (skippedDirs.Any(dir =>
                    relative.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            if (!extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            if (File.ReadAllText(path).Contains("maplestory", StringComparison.OrdinalIgnoreCase))
            {
                survivors.Add(relative);
            }
        }

        Assert.True(survivors.Count == 0,
            "Maplestory was removed by the EFT font stack change; stale references:\n"
            + string.Join("\n", survivors));

        // The unreferenced duplicate at the repo root is gone too.
        Assert.False(File.Exists(Path.Combine(TestRepo.Root(), "fonts", "Maplestory Light.ttf")),
            "The repo-root fonts/Maplestory Light.ttf duplicate must stay deleted");
    }

    /// <summary>
    /// Row heights come from the composite chain's line box, which WPF takes from the
    /// *leading* family (Bender, 1.130 em). If a CJK face ever led a chain, every row in
    /// the app would grow ~28% (Noto's line spacing is 1.448 em): a silent, app-wide
    /// layout change no other assertion here would catch.
    /// </summary>
    [Fact]
    public void Every_chain_takes_its_line_box_from_the_leading_latin_face()
    {
        var bender = new FontFamily(new Uri(FontsDir() + Path.DirectorySeparatorChar), "./#Bender");

        foreach (var language in Enum.GetValues<AppLanguage>())
        {
            var chain = ChainFamily(language);
            Assert.Equal(bender.LineSpacing, chain.LineSpacing, 4);
            Assert.Equal(bender.Baseline, chain.Baseline, 4);
        }
    }

    /// <summary>
    /// The clipping risk the spec flags: the composite's line box is Latin-derived while
    /// the CJK faces are internally taller. Measured, CJK ink stays inside the vertical
    /// envelope the chain's own accented Latin already occupies, at every size the app can
    /// render (FontSizeTiny at the smallest base size through SettingsService.MaxFontSize),
    /// so no container sized for Latin can clip CJK. A chain reorder or a taller CJK face
    /// fails here instead of waiting for a manual KO/JA sweep.
    ///
    /// EN/KO only: every face this touches (Bender for the Latin sample, Noto for the CJK
    /// samples) is embedded, so the numbers are machine-independent. The JA chain resolves
    /// through system Meiryo/Yu Gothic and cannot be pinned this way.
    /// </summary>
    [Theory]
    [InlineData("전체 타입 힣궯")]    // Hangul, including tall jamo clusters
    [InlineData("調査あアぱポ")]      // kanji + kana (quest text)
    [InlineData("（）｛｝［］「」")]  // full-width brackets, the tallest ink in the CJK face
    public void Cjk_ink_stays_inside_the_chains_latin_ink_envelope(string cjkSample)
    {
        // Accented caps plus descenders: the tallest ink ordinary Latin/Cyrillic UI text
        // produces, and the envelope the existing row heights were already sized around.
        const string latinSample = "ÅÉgjpqЁ";
        var chain = ChainFamily(AppLanguage.EN);

        for (var emSize = SettingsService.MinFontSize - 6; emSize <= SettingsService.MaxFontSize; emSize++)
        {
            var latin = Measure(chain, latinSample, emSize);
            var cjk = Measure(chain, cjkSample, emSize);

            Assert.True(cjk.Extent <= latin.Extent,
                $"At {emSize}px, CJK ink ({cjk.Extent:F2}px) exceeds the chain's Latin ink "
                + $"({latin.Extent:F2}px); containers sized for Latin can now clip CJK.");
        }
    }

    /// <summary>
    /// Every font family the app names must be one of the three the design records: the
    /// app-wide chain (AppFont), the icon glyph font (IconFont), and the deliberate
    /// Consolas map-coordinate readout, the two exceptions listed in the spec's
    /// Non-Goals. The Maplestory word-ban above catches the face that was removed; this
    /// catches the next hardcoded face before it spreads to 160 sites.
    /// </summary>
    [Fact]
    public void Every_fontfamily_literal_in_the_app_is_an_approved_family()
    {
        var approved = new[]
        {
            "{DynamicResource AppFont}",
            "{StaticResource IconFont}",
            "Consolas",
        };

        var offenders = new List<string>();
        foreach (var path in AppProjectFiles("*.xaml"))
        {
            var xaml = File.ReadAllText(path);
            var values = Regex.Matches(xaml, "FontFamily=\"(?<value>[^\"]*)\"")
                .Concat(Regex.Matches(xaml, "Property=\"FontFamily\"\\s+Value=\"(?<value>[^\"]*)\""))
                .Select(match => match.Groups["value"].Value);

            offenders.AddRange(values
                .Where(value => !approved.Contains(value, StringComparer.Ordinal))
                .Select(value => $"{Path.GetRelativePath(Path.Combine(TestRepo.Root(), "TarkovHelper"), path)}: {value}"));
        }

        Assert.True(offenders.Count == 0,
            "FontFamily literals outside the approved set (AppFont / IconFont / Consolas):\n"
            + string.Join("\n", offenders));
    }

    /// <summary>
    /// FontStacks.CreateFontFamily documents itself as the single construction path
    /// because a FontFamily built without FontStacks.PackBaseUri silently drops every
    /// embedded ./Fonts/# face and falls back to system fonts, and WPF never throws for an
    /// unresolvable token. Nothing enforced that invariant; this does.
    /// </summary>
    [Fact]
    public void Fontstacks_is_the_only_place_that_constructs_a_fontfamily()
    {
        var offenders = AppProjectFiles("*.cs")
            .Where(path => !string.Equals(Path.GetFileName(path), "FontStacks.cs", StringComparison.Ordinal))
            .Where(path => Regex.IsMatch(File.ReadAllText(path), @"new\s+FontFamily\s*\("))
            .Select(path => Path.GetRelativePath(Path.Combine(TestRepo.Root(), "TarkovHelper"), path))
            .ToList();

        Assert.True(offenders.Count == 0,
            "FontFamily is constructed outside FontStacks (use FontStacks.CreateFontFamily "
            + "so the pack base URI is attached):\n" + string.Join("\n", offenders));
    }

    /// <summary>The app's chain for a language, resolved against the source tree.</summary>
    private static FontFamily ChainFamily(AppLanguage language) =>
        new(new Uri(Path.Combine(TestRepo.Root(), "TarkovHelper") + Path.DirectorySeparatorChar),
            FontStacks.ForLanguage(language));

    private static FormattedText Measure(FontFamily family, string text, double emSize) =>
        new(text, CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            emSize, System.Windows.Media.Brushes.Black, pixelsPerDip: 1.0);

    /// <summary>Source files of the app project, excluding build output.</summary>
    private static IEnumerable<string> AppProjectFiles(string pattern)
    {
        var projectRoot = Path.Combine(TestRepo.Root(), "TarkovHelper");
        return Directory.EnumerateFiles(projectRoot, pattern, SearchOption.AllDirectories)
            .Where(path =>
            {
                var relative = Path.GetRelativePath(projectRoot, path);
                return !relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && !relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            });
    }
}
