using System.Windows;
using System.Windows.Media;
using TarkovHelper.Services;
// The test project also references WinForms (for TarkovDBEditor); disambiguate.
using FontFamily = System.Windows.Media.FontFamily;

namespace TarkovHelper.Tests;

/// <summary>
/// Guards the half of the font stack the string and asset tests cannot see: that the
/// chains' relative ./Fonts/#Family tokens actually RESOLVE to the embedded faces
/// through a pack URI. WPF never throws for an unresolvable token: it substitutes a
/// system font silently, so a renamed Fonts folder, a misspelt family, or a wrong base
/// URI would otherwise ship green and render in Segoe UI.
///
/// Addressing: in the running app WPF pins Application.ResourceAssembly to TarkovHelper,
/// so pack://application:,,,/ already means "this app's resources". The test host's
/// entry assembly is testhost, so the same resources are named explicitly with the
/// ;component form, derived from FontStacks.PackBaseUri, so a wrong scheme or
/// authority there fails here too.
/// </summary>
public sealed class FontPackUriTests
{
    private static readonly Uri TestHostPackBase = CreateTestHostPackBase();

    private static Uri CreateTestHostPackBase()
    {
        // Touching the Application type runs the WPF initialization that registers
        // pack:// request handling; without it every pack URI below resolves to
        // nothing, silently, because font resolution never throws.
        _ = System.Windows.Application.ResourceAssembly;
        return new Uri(FontStacks.PackBaseUri, typeof(App).Assembly.GetName().Name + ";component/");
    }

    private static IEnumerable<string> EmbeddedTokens() =>
        Enum.GetValues<AppLanguage>()
            .Select(FontStacks.ForLanguage)
            .SelectMany(chain => chain.Split(','))
            .Select(token => token.Trim())
            .Where(token => token.StartsWith("./", StringComparison.Ordinal))
            .Distinct();

    [Fact]
    public void Every_embedded_chain_token_resolves_to_an_embedded_face()
    {
        var tokens = EmbeddedTokens().ToList();
        Assert.NotEmpty(tokens);

        foreach (var token in tokens)
        {
            var expectedFamily = token[(token.IndexOf('#') + 1)..];
            var family = new FontFamily(TestHostPackBase, token);

            foreach (var weight in new[] { FontWeights.Normal, FontWeights.Bold })
            {
                var typeface = new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal);
                Assert.True(typeface.TryGetGlyphTypeface(out var glyphTypeface),
                    $"chain token '{token}' ({weight}) did not resolve through {TestHostPackBase}");
                Assert.Contains(expectedFamily, glyphTypeface.FamilyNames.Values);
                Assert.StartsWith(TestHostPackBase.ToString(), glyphTypeface.FontUri.ToString(),
                    StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void A_token_pointing_at_a_missing_folder_resolves_to_nothing()
    {
        // Negative control: proves the assertions above can fail. ./Typefaces/ is the
        // shape a renamed Fonts folder would leave behind in the chain.
        var family = new FontFamily(TestHostPackBase, "./Typefaces/#Bender");
        var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        Assert.False(typeface.TryGetGlyphTypeface(out _));
    }
}
