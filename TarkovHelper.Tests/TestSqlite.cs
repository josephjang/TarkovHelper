using System.IO;
using Microsoft.Data.Sqlite;

namespace TarkovHelper.Tests;

/// <summary>
/// Throwaway SQLite databases for the suites that need real database BYTES rather than a
/// database file: the data channel serves databases as payloads, so its fixtures have to be
/// something SQLite can actually open, and every one of them is built only to be read back
/// into a <c>byte[]</c> and thrown away.
/// <para>
/// Centralised for the reason <see cref="TempStoreRoot"/> is, and against the same failure.
/// <c>Microsoft.Data.Sqlite</c> pools connections per connection string, so a database whose
/// last statement has returned still holds its file open: without the
/// <see cref="SqliteConnection.ClearAllPools"/> call below, the read-back races a handle that
/// is still open and the cleanup afterwards silently fails on Windows. Three suites had each
/// grown their own copy of "temp folder, open, execute, clear the pools, read the bytes", and
/// a fourth copy that dropped the pool flush would still pass every assertion it makes.
/// <see cref="DataFormatDriftTests"/> documents the same hazard from the reading side.
/// </para>
/// </summary>
internal static class TestSqlite
{
    /// <summary>
    /// Runs <paramref name="sql"/> against a fresh database and returns the resulting file as
    /// bytes. The file itself lives in a temp folder that is deleted before this returns: the
    /// bytes are what the caller wanted, and the file was only a way to get them.
    /// </summary>
    /// <param name="sql">
    /// Executed as one batch, so several statements separated by <c>;</c> are fine. This is
    /// what makes the fixture distinguishable (a marker table) or meaningful (a
    /// <c>PRAGMA user_version</c> stamp).
    /// </param>
    /// <param name="seed">
    /// Existing database bytes to start from, for the fixtures that mean "this database, but
    /// with one more statement applied to it". Null builds from an empty database.
    /// </param>
    internal static byte[] BuildDatabase(string sql, byte[]? seed = null)
    {
        var folder = Directory.CreateTempSubdirectory("tarkovhelper-sqlite-fixture");
        try
        {
            return BuildDatabaseAt(Path.Combine(folder.FullName, "fixture.db"), sql, seed);
        }
        finally
        {
            try { folder.Delete(recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// The same build, at a path the caller owns and cleans up. Split out from
    /// <see cref="BuildDatabase"/> so the pool flush is provable: a caller that knows the path
    /// can try to delete the file the instant this returns, which is the one observable
    /// difference the <see cref="SqliteConnection.ClearAllPools"/> call makes. Without that
    /// seam the flush could be dropped and every assertion in this assembly would still pass.
    /// </summary>
    internal static byte[] BuildDatabaseAt(string path, string sql, byte[]? seed = null)
    {
        if (seed != null) File.WriteAllBytes(path, seed);

        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        // Not optional: see the type's own remarks.
        SqliteConnection.ClearAllPools();

        return File.ReadAllBytes(path);
    }

    /// <summary>
    /// The data format a database declares in its own header. Read back through SQLite rather
    /// than by peeking at the file header, so this proves SQLite itself agrees the stamp is
    /// set.
    /// </summary>
    internal static int ReadDataFormatStamp(string databasePath)
    {
        int stamp;
        using (var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version";
            stamp = Convert.ToInt32(command.ExecuteScalar());
        }
        // The caller's next step is usually to replace or delete this file.
        SqliteConnection.ClearAllPools();

        return stamp;
    }
}
