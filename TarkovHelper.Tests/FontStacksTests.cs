using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// Guards the per-language chain ordering decisions recorded in
/// feature-eft-font-stack.spec.md: the embedded Bender (the game's
/// Latin/Cyrillic face) leads every chain with the embedded Play right after
/// it as glyph backstop, and Meiryo must precede the bundled Noto for
/// Japanese while Korean keeps Noto ahead of every system Japanese font.
///
/// Assertions compare whole comma-split tokens, not substrings: "Yu Gothic" is
/// a prefix of "Yu Gothic UI", so IndexOf over the raw chain string would
/// silently match the wrong family.
/// </summary>
public sealed class FontStacksTests
{
    private const string BenderToken = "./Fonts/#Bender";
    private const string PlayToken = "./Fonts/#Play";
    private const string NotoToken = "./Fonts/#Noto Sans CJK KR";

    private static List<string> Tokens(AppLanguage language) =>
        FontStacks.ForLanguage(language).Split(',').Select(token => token.Trim()).ToList();

    [Fact]
    public void Every_language_yields_a_chain_ending_in_segoe_ui()
    {
        foreach (var language in Enum.GetValues<AppLanguage>())
        {
            var tokens = Tokens(language);
            Assert.NotEmpty(tokens);
            // Segoe UI is the last *named* family. (It does not terminate
            // resolution: WPF's global composite fallback still runs for
            // anything no named family covers.)
            Assert.Equal("Segoe UI", tokens[^1]);
        }
    }

    [Fact]
    public void Every_chain_leads_with_bundled_bender_then_play()
    {
        foreach (var language in Enum.GetValues<AppLanguage>())
        {
            var tokens = Tokens(language);
            Assert.Equal(BenderToken, tokens[0]);
            Assert.Equal(PlayToken, tokens[1]);
        }
    }

    [Fact]
    public void En_and_ko_share_a_chain_with_noto_before_system_japanese_fonts()
    {
        var en = FontStacks.ForLanguage(AppLanguage.EN);
        var ko = FontStacks.ForLanguage(AppLanguage.KO);
        Assert.Equal(en, ko);

        // Korean mode must not let a Japanese-form system font capture hanja or
        // full-width punctuation ahead of the game's own Korean face. The only
        // Japanese-form family in this chain is "Yu Gothic UI" (the JA fonts
        // Meiryo and bare "Yu Gothic" must not appear at all).
        var tokens = Tokens(AppLanguage.KO);
        var noto = tokens.IndexOf(NotoToken);
        var yuGothicUi = tokens.IndexOf("Yu Gothic UI");
        Assert.True(noto >= 0, "EN/KO chain must embed Noto: " + ko);
        Assert.True(yuGothicUi >= 0, "EN/KO chain must carry Yu Gothic UI: " + ko);
        Assert.True(yuGothicUi > noto,
            "EN/KO chain must order the bundled Noto ahead of Yu Gothic UI: " + ko);
        Assert.DoesNotContain("Meiryo", tokens);
        Assert.DoesNotContain("Yu Gothic", tokens);
    }

    [Fact]
    public void Ja_chain_orders_meiryo_then_yu_gothic_then_noto()
    {
        var ja = FontStacks.ForLanguage(AppLanguage.JA);
        var tokens = Tokens(AppLanguage.JA);
        var meiryo = tokens.IndexOf("Meiryo");
        var yuGothic = tokens.IndexOf("Yu Gothic");
        var noto = tokens.IndexOf(NotoToken);

        Assert.True(meiryo >= 0, "JA chain must reference system Meiryo: " + ja);
        Assert.True(yuGothic > meiryo,
            "Yu Gothic is the fallback for machines without Meiryo and must follow it: " + ja);
        Assert.True(noto > yuGothic,
            "The bundled Noto is the JA last-resort CJK face and must follow both: " + ja);
    }

    [Fact]
    public void Adding_a_language_forces_a_deliberate_chain_decision()
    {
        // ForLanguage's default arm hands any unlisted language the EN/KO chain, which
        // puts the Korean-form Noto ahead of every system CJK face, right for a Latin
        // language, wrong for one needing Japanese or Chinese forms (ZH is a PRD
        // non-goal today, not an impossibility). Nothing else fails if that inheritance
        // is wrong: WPF renders the wrong forms silently, and the per-language loops
        // above stay green. When this fails, decide explicitly whether the new language
        // gets its own arm in FontStacks.ForLanguage, then update this list.
        Assert.Equal(
            new[] { AppLanguage.EN, AppLanguage.KO, AppLanguage.JA },
            Enum.GetValues<AppLanguage>());
    }

    [Fact]
    public void Pack_base_uri_constructs_without_wpf_initialization()
    {
        // Guards FontStacks against WPF load order: the "pack" UriParser is registered
        // by WPF's static init, so a host that has not touched WPF (a filtered test
        // run) used to fail this type's initializer with UriFormatException.
        Assert.Equal("pack://application:,,,/", FontStacks.PackBaseUri.ToString());
    }
}
