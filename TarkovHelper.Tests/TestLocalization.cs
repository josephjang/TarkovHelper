using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// Builds a LocalizationService pinned to a language without touching user_data.db:
/// the real constructor opens the DB via UserDataDbService, but the string properties
/// only read _currentLanguage, so an uninitialized instance with that field set is a
/// faithful string source. Single home for the pattern (previously copied into
/// LocalizationHeaderStringsTests and QuestCompletionCascadeTests).
/// </summary>
internal static class TestLocalization
{
    internal static LocalizationService WithLanguage(AppLanguage language)
    {
        var loc = TestReflection.Uninitialized<LocalizationService>();
        TestReflection.SetPrivateField(loc, "_currentLanguage", language);
        return loc;
    }
}
