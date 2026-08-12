using System.IO;

namespace TarkovHelper.Tests;

/// <summary>
/// Unit guards for AppDriver.RemoveLegacyLanguageOverride, the harness step that
/// deletes a leftover legacy Data\settings.json next to the app under test so a
/// stale language override cannot flip e2e text assertions to KO/JA (see the
/// helper's own doc-comment for why TARKOVHELPER_CONFIG_PATH cannot isolate it).
/// Plain unit tests: no app launch, just a temp directory.
/// </summary>
public sealed class E2EHarnessIsolationTests : IDisposable
{
    private readonly string _appDir =
        Path.Combine(Path.GetTempPath(), "TarkovHelperHarnessTests", Guid.NewGuid().ToString("N"));

    public E2EHarnessIsolationTests() => Directory.CreateDirectory(_appDir);

    public void Dispose()
    {
        try { Directory.Delete(_appDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Deletes_a_leftover_legacy_settings_file()
    {
        var dataDir = Path.Combine(_appDir, "Data");
        Directory.CreateDirectory(dataDir);
        var legacy = Path.Combine(dataDir, "settings.json");
        File.WriteAllText(legacy, """{"language":"KO"}""");

        AppDriver.RemoveLegacyLanguageOverride(_appDir);

        Assert.False(File.Exists(legacy), "the legacy language override was not deleted");
    }

    [Fact]
    public void Is_a_no_op_when_the_file_is_already_gone()
    {
        // Data\ exists but holds no settings.json (the state right after a first
        // app launch migrated and deleted it): a second call must not throw.
        Directory.CreateDirectory(Path.Combine(_appDir, "Data"));

        AppDriver.RemoveLegacyLanguageOverride(_appDir);
        AppDriver.RemoveLegacyLanguageOverride(_appDir);
    }

    [Fact]
    public void Tolerates_a_nonexistent_app_directory()
    {
        AppDriver.RemoveLegacyLanguageOverride(Path.Combine(_appDir, "does-not-exist"));
    }
}
