using System.IO;
using System.Text.RegularExpressions;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// Pure helpers behind the update UI (top-bar redesign): the version formatting
/// shared by the title-bar chip and the Settings section, the status-kind
/// mapping that keeps a failed re-check visible while an update found by an
/// earlier successful check remains installable, and the Settings status wording
/// for an available update. Ends with a source-level guard on the title-bar pill's
/// XAML, whose colours are code-behind decisions and must not be re-declared there.
/// </summary>
public class UpdateStatusTests
{
    [Theory]
    [InlineData("2026.7.0", "v2026.7.0")]
    [InlineData("2026.7.0.4", "v2026.7.0")] // 4-part assembly version trims to 3
    [InlineData("2026.8", "v2026.8")]       // 2-part version must not throw (ToString(3) would)
    public void FormatVersion_renders_three_parts_when_available(string input, string expected)
        => Assert.Equal(expected, UpdateService.FormatVersion(Version.Parse(input)));

    [Fact]
    public void No_completed_check_yet_is_none()
        => Assert.Equal(UpdateStatusKind.None,
            UpdateService.GetStatusKind(isChecking: false, lastCheckFailed: false, updateAvailable: false, hasCompletedCheck: false));

    [Fact]
    public void Checking_wins_over_every_other_state()
    {
        Assert.Equal(UpdateStatusKind.Checking,
            UpdateService.GetStatusKind(isChecking: true, lastCheckFailed: false, updateAvailable: false, hasCompletedCheck: false));
        Assert.Equal(UpdateStatusKind.Checking,
            UpdateService.GetStatusKind(isChecking: true, lastCheckFailed: true, updateAvailable: true, hasCompletedCheck: true));
    }

    [Fact]
    public void Failed_recheck_stays_visible_even_when_an_update_is_known()
        => Assert.Equal(UpdateStatusKind.Failed,
            UpdateService.GetStatusKind(isChecking: false, lastCheckFailed: true, updateAvailable: true, hasCompletedCheck: true));

    [Fact]
    public void Update_available_after_a_successful_check()
        => Assert.Equal(UpdateStatusKind.UpdateAvailable,
            UpdateService.GetStatusKind(isChecking: false, lastCheckFailed: false, updateAvailable: true, hasCompletedCheck: true));

    [Fact]
    public void Up_to_date_after_a_successful_check()
        => Assert.Equal(UpdateStatusKind.UpToDate,
            UpdateService.GetStatusKind(isChecking: false, lastCheckFailed: false, updateAvailable: false, hasCompletedCheck: true));

    #region Settings status wording

    // A superseded build's data channel has stopped, and installing the offered update is the
    // only thing that restarts it. So the Settings line says that instead of the ordinary
    // "an update is available", which a player has learned to postpone.
    [Theory]
    [InlineData(AppLanguage.EN)]
    [InlineData(AppLanguage.KO)]
    [InlineData(AppLanguage.JA)]
    public void A_superseded_build_says_data_updates_have_ended(AppLanguage language)
    {
        var loc = TestLocalization.WithLanguage(language);

        Assert.Equal(loc.UpdateStatusDataEnded,
            SettingsUpdateStatus.AvailableText(loc, isSuperseded: true));
    }

    [Theory]
    [InlineData(AppLanguage.EN)]
    [InlineData(AppLanguage.KO)]
    [InlineData(AppLanguage.JA)]
    public void An_ordinary_available_update_keeps_the_ordinary_wording(AppLanguage language)
    {
        var loc = TestLocalization.WithLanguage(language);

        Assert.Equal(loc.UpdateStatusAvailable,
            SettingsUpdateStatus.AvailableText(loc, isSuperseded: false));
    }

    // The escalation only means something if the two readings differ in every language.
    [Theory]
    [InlineData(AppLanguage.EN)]
    [InlineData(AppLanguage.KO)]
    [InlineData(AppLanguage.JA)]
    public void The_superseded_status_differs_from_the_ordinary_one(AppLanguage language)
    {
        var loc = TestLocalization.WithLanguage(language);

        Assert.NotEqual(
            SettingsUpdateStatus.AvailableText(loc, isSuperseded: false),
            SettingsUpdateStatus.AvailableText(loc, isSuperseded: true));
    }

    [Fact]
    public void The_settings_status_rejects_a_missing_localization_source()
        => Assert.Throws<ArgumentNullException>(
            () => SettingsUpdateStatus.AvailableText(null!, isSuperseded: false));

    #endregion

    #region Source-level guard on the pill's XAML

    // UpdateVersionChipUI assigns BtnVersionChip's Background and Foreground on every path that
    // makes the button visible (green for an ordinary update, amber when the data channel is
    // superseded), so a colour declared on the element in XAML can never be observed: a
    // maintainer who edits it would see no run-time change. Its Settings twin
    // BtnUpdateAvailableSettings keeps its declared colours precisely because no code writes
    // them. Only the Button's own start tag is inspected, since its child TextBlocks
    // legitimately bind Foreground to the button.
    [Fact]
    public void The_title_bar_pill_declares_no_colours_in_xaml()
    {
        var startTag = ButtonStartTag("BtnVersionChip");

        Assert.DoesNotContain("Background=", startTag);
        Assert.DoesNotContain("Foreground=", startTag);
    }

    // The same tag's AutomationProperties.Name is written by UpdateVersionChipUI too (the
    // visible label, per WCAG 2.5.3), so a static one here would be equally dead.
    [Fact]
    public void The_title_bar_pill_declares_no_automation_name_in_xaml()
        => Assert.DoesNotContain("AutomationProperties.Name=", ButtonStartTag("BtnVersionChip"));

    // The twin is the control group: nothing in code writes its colours, so its declaration is
    // live and must stay. Without this the guard above would also pass if someone "fixed" the
    // asymmetry by stripping both.
    [Fact]
    public void The_settings_install_button_still_declares_its_colours_in_xaml()
    {
        var startTag = ButtonStartTag("BtnUpdateAvailableSettings");

        Assert.Contains("Background=", startTag);
        Assert.Contains("Foreground=", startTag);
    }

    // The pill's two states used to be decided twice over: HeaderUpdatePill.For branched on
    // isSuperseded for the words, and UpdateVersionChipUI branched on the same flag again for
    // the brushes, so a third pill state could ship with the escalated wording and the
    // reassuring green tone. The renderer now reads the tone the pill carries, which leaves
    // exactly one mention of the flag in the method: the argument handed to For.
    [Fact]
    public void The_chip_renderer_takes_its_tone_from_the_pill_rather_than_re_deciding_it()
    {
        var body = MemberSource("private void UpdateVersionChipUI()");

        Assert.Contains("pill.Tone", body, StringComparison.Ordinal);

        var mentions = Regex.Matches(body, "IsSuperseded|isSuperseded").Count;
        Assert.True(mentions == 1,
            $"UpdateVersionChipUI mentions the superseded flag {mentions} times; it should only "
            + "hand it to HeaderUpdatePill.For and render the tone that comes back.");
    }

    /// <summary>
    /// Everything from the named declaration up to the doc comment of the next member,
    /// the same slicing <see cref="MainWindowDataUpdateHandlerTests"/> pins its handler with.
    /// </summary>
    private static string MemberSource(string signature)
    {
        var source = File.ReadAllText(Path.Combine(
            TestRepo.Root(), "TarkovHelper", "MainWindow.xaml.cs"));

        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' no longer exists; update this test with it.");

        var next = source.IndexOf("\n    /// <summary>", start, StringComparison.Ordinal);
        return next < 0 ? source[start..] : source[start..next];
    }

    /// <summary>
    /// The named Button's own start tag from MainWindow.xaml, attributes only: from
    /// "&lt;Button x:Name=..." up to the first "&gt;", so nested elements are excluded.
    /// </summary>
    private static string ButtonStartTag(string name)
    {
        var xaml = File.ReadAllText(Path.Combine(
            TestRepo.Root(), "TarkovHelper", "MainWindow.xaml"));

        var match = Regex.Match(xaml, $@"<Button\s+x:Name=""{Regex.Escape(name)}""[^>]*>");
        Assert.True(match.Success, $"MainWindow.xaml has no Button named '{name}'");
        return match.Value;
    }

    #endregion
}
