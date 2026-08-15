using System.IO;
using Microsoft.Data.Sqlite;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// A throwaway folder for the suites that drive a REAL <see cref="UserDataDbService"/>, plus the
/// teardown that gets the folder back off the disk again.
/// <para>
/// Centralised for the reason <see cref="TestReflection"/> is: six suites had each grown their own
/// copy of "temp folder, one .db per test, delete it in Dispose", in two dialects, and the copies
/// are the dangerous kind. <c>Microsoft.Data.Sqlite</c> pools connections per connection string,
/// so a store whose last statement has returned still holds its file open. A copy that loses the
/// <see cref="SqliteConnection.ClearAllPools"/> line below still passes every one of its
/// assertions, because the delete it breaks is swallowed as an <see cref="IOException"/> - Windows
/// refuses to remove a file another handle has open. The only symptom is that the suite quietly
/// leaves one database per test behind in %TEMP% forever. That line is the whole reason this type
/// exists, so it lives in one place where it cannot be dropped from five of six copies unnoticed.
/// </para>
/// <para>
/// Composition rather than a base class, so a suite with its own constructor, fixture or base type
/// can still use it: a field, a one line <c>Dispose</c> and (where the suite wants the old name) a
/// one line <c>NewStore</c>.
/// </para>
/// </summary>
internal sealed class TempStoreRoot : IDisposable
{
    /// <summary>The folder every store and subfolder handed out here lives under.</summary>
    internal string Root { get; }

    /// <param name="label">
    /// Names the suite in the folder name, so a folder that outlives its <see cref="Dispose"/>
    /// (a store still holding its file, a test host killed mid-run) says which suite left it.
    /// </param>
    internal TempStoreRoot(string label)
    {
        Root = Path.Combine(
            Path.GetTempPath(), "tarkovhelper-" + label + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    /// <summary>
    /// A store on its own fresh database file, so nothing one test writes can be read by another.
    /// The file itself is created by the store on its first write.
    /// </summary>
    internal UserDataDbService NewStore()
        => new(Path.Combine(Root, Guid.NewGuid().ToString("N") + ".db"));

    /// <summary>
    /// A fresh empty subfolder, for the cases that need a directory rather than a database file:
    /// a Config folder to point <c>AppEnv.ConfigPath</c> at, a folder to drop an
    /// app_settings.json into, or a path SQLite cannot open as a file.
    /// </summary>
    internal string NewFolder(string label)
    {
        var folder = Path.Combine(Root, label + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>
    /// Returns the whole tree to the disk. The pool flush comes first and is not optional: see
    /// the type's own remarks.
    /// </summary>
    public void Dispose()
    {
        if (!Directory.Exists(Root)) return;

        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
