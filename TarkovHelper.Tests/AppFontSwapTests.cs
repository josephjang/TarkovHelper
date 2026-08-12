using System.Windows.Media;
using TarkovHelper.Services;
// The test project also references WinForms (for TarkovDBEditor); disambiguate.
using FontFamily = System.Windows.Media.FontFamily;

namespace TarkovHelper.Tests;

/// <summary>
/// Guards the runtime half of the EFT font stack: App.ApplyFontStack must
/// actually swap the AppFont resource to the requested language's chain with
/// the pack base URI attached. FontStacksTests pins the pure chain strings;
/// without this, dropping the swap (or writing the wrong key/URI) would ship
/// with a green suite.
///
/// WPF allows one Application per AppDomain, so a single shared App instance
/// serves every test in this class. App's ctor does no WPF bootstrapping;
/// the startup wiring lives in OnStartup, which never runs here.
/// </summary>
public sealed class AppFontSwapTests
{
    private static readonly Lazy<App> SharedApp = new(() => new App());

    [Theory]
    [InlineData(AppLanguage.EN)]
    [InlineData(AppLanguage.KO)]
    [InlineData(AppLanguage.JA)]
    public void ApplyFontStack_swaps_the_appfont_resource_to_the_language_chain(AppLanguage language)
    {
        var app = SharedApp.Value;

        app.ApplyFontStack(language);

        var family = Assert.IsType<FontFamily>(app.Resources["AppFont"]);
        Assert.Equal(FontStacks.ForLanguage(language), family.Source);
        Assert.Equal(FontStacks.PackBaseUri, family.BaseUri);
    }

    [Fact]
    public void ApplyFontStack_switch_and_back_restores_the_en_chain()
    {
        var app = SharedApp.Value;

        app.ApplyFontStack(AppLanguage.JA);
        app.ApplyFontStack(AppLanguage.EN);

        var family = Assert.IsType<FontFamily>(app.Resources["AppFont"]);
        Assert.Equal(FontStacks.ForLanguage(AppLanguage.EN), family.Source);
    }
}
