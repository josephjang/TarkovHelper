using System.IO;

namespace TarkovHelper.Tests;

/// <summary>
/// The one guard on the shared temp-store scaffolding, and it is the guard the six copies it
/// replaced never had: that the folder is actually gone afterwards.
/// </summary>
public sealed class TempStoreRootTests
{
    // Not tautological, and the reason is the pooling: with ClearAllPools() removed from Dispose
    // this fails on Windows, because the connection the write went through is still in the pool
    // holding the file open, Directory.Delete throws IOException and Dispose swallows it. A store
    // that has WRITTEN is therefore essential to the case - an unused store never opens its file.
    [Fact]
    public async Task Dispose_removes_the_folder_after_a_store_has_written_to_it()
    {
        string root;
        using (var stores = new TempStoreRoot("selftest"))
        {
            root = stores.Root;
            var store = stores.NewStore();
            await store.SetProfileSettingAsync("pvp", "app.playerLevel", "42");
            Assert.True(Directory.Exists(root));
        }

        Assert.False(Directory.Exists(root), $"{root} survived Dispose");
    }

    // The subfolders go with it, including one holding a file, so a suite that points
    // AppEnv.ConfigPath at NewFolder leaves nothing behind either.
    [Fact]
    public void Dispose_removes_the_subfolders_handed_out_along_the_way()
    {
        string root;
        string folder;
        using (var stores = new TempStoreRoot("selftest"))
        {
            root = stores.Root;
            folder = stores.NewFolder("config");
            Assert.True(Directory.Exists(folder));
            File.WriteAllText(Path.Combine(folder, "app_settings.json"), "{}");
        }

        Assert.False(Directory.Exists(folder));
        Assert.False(Directory.Exists(root));
    }

    // Every store and every folder is its own, so nothing one test writes is visible to the next.
    [Fact]
    public async Task Each_store_and_folder_is_separate_from_the_last()
    {
        using var stores = new TempStoreRoot("selftest");

        var first = stores.NewStore();
        var second = stores.NewStore();
        await first.SetProfileSettingAsync("pvp", "app.playerLevel", "42");

        Assert.Null(await second.GetProfileSettingAsync("pvp", "app.playerLevel"));
        Assert.NotEqual(stores.NewFolder("config"), stores.NewFolder("config"));
    }

    // The label is in the folder name, which is the only thing identifying a folder that outlives
    // its Dispose.
    [Fact]
    public void The_root_is_named_after_the_suite_that_owns_it()
    {
        using var stores = new TempStoreRoot("selftest");

        Assert.StartsWith(
            Path.Combine(Path.GetTempPath(), "tarkovhelper-selftest-"), stores.Root);
    }
}
