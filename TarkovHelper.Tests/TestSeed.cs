namespace TarkovHelper.Tests;

/// <summary>
/// The seed database the fixtures are derived from: the app's own bundled seed, read in
/// place rather than through a copy of it.
///
/// TarkovHelper.csproj links data/v$(TarkovDataFormatVersion)/tarkov_data.db to
/// Assets\tarkov_data.db, and the ProjectReference flows that copy into this assembly's
/// output. Reading it where it lands is what makes every fixture describe exactly the
/// database the app under test loads: there is no second copy that can go stale, and no
/// source path restated here that could keep naming a directory the live data format has
/// left behind.
///
/// TarkovHelper.Tests.csproj fails the build if the app stops bundling the file, so a
/// missing seed surfaces as a build error rather than as fixtures that quietly find nothing.
/// </summary>
internal static class TestSeed
{
    /// <summary>
    /// Absolute path to the bundled seed database in this assembly's output directory.
    /// </summary>
    internal static string DatabasePath =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "tarkov_data.db");
}
