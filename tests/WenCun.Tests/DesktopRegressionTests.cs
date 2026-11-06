using System.Reflection;
using WenCun.Core.Domain;
using WenCun.Core.Pending;
using WenCun.Core.Storage;
using WenCun.Desktop.Infrastructure;
using WenCun.Desktop.ViewModels;

namespace WenCun.Tests;

public sealed class DesktopRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WenCunDesktopTests", Guid.NewGuid().ToString("N"));
    private PendingStore Store => new(_root);
    private Library Library => new(Path.Combine(_root, "library"));
    private static PendingArticle Item => new("id", "biz", "公众号甲", "标题", "作者", "https://mp.weixin.qq.com/s/test", null, DateTimeOffset.UtcNow);

    [Fact]
    public void AccountsPageIncludesDiscoveredButNotSavedAccounts()
    {
        Store.Merge("biz", "公众号甲", [Item], 10, true);
        var vm = new AccountsViewModel(Library, Store, _ => { }, _ => { }, _ => Task.CompletedTask,
            _ => Task.CompletedTask, _ => Task.CompletedTask, _ => { });
        vm.Load();
        var account = Assert.Single(vm.Items);
        Assert.Equal("公众号甲", account.Name);
        Assert.Equal(1, account.Pending);
        Assert.True(account.Account.Complete);
    }

    [Fact]
    public void RefreshDuringDiscoveryKeepsExistingSelection()
    {
        Store.Merge("biz", "公众号甲", [Item], 10, false);
        var vm = new PendingViewModel(Store, _ => null, _ => { }, _ => { }, _ => { });
        vm.Load();
        vm.Items[0].Selected = true;
        Store.Merge("biz", "公众号甲", [Item with { Id = "second", Title = "第二篇" }], 20, true);
        vm.Load();
        Assert.Equal(2, vm.Items.Count);
        Assert.True(vm.Items.Single(row => row.Article.Id == "id").Selected);
        Assert.False(vm.Items.Single(row => row.Article.Id == "second").Selected);
        Assert.Equal("保存所选（1 篇）", vm.SaveText);
        vm.AllSelected = true;
        Assert.Equal("保存所选（2 篇）", vm.SaveText);
        vm.AllSelected = false;
        Assert.Equal("保存所选", vm.SaveText);
    }

    [Fact]
    public void MainWindowStartsAtTheUsefulEntryPoint()
    {
        var empty = new MainViewModel(Library, _root, null);
        Assert.Equal("Collect", empty.Section);
        Store.Merge("biz", "公众号甲", [Item], 10, false);
        var pending = new MainViewModel(Library, _root, null);
        Assert.Equal("Library", pending.Section);
    }

    [Fact]
    public void CannotSwitchLibraryDuringSaveOrExport()
    {
        var library = Library;
        var vm = new MainViewModel(library, _root, null);
        var gate = (SemaphoreSlim)typeof(MainViewModel).GetField("_workGate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!;
        gate.Wait();
        try
        {
            vm.SwitchLibrary(Path.Combine(_root, "other"));
            Assert.Same(library, vm.Library);
            Assert.False(Directory.Exists(Path.Combine(_root, "other")));
        }
        finally { gate.Release(); }
    }

    [Fact]
    public void ConfigSaveIsAtomicAndMalformedFileFallsBack()
    {
        var root = Path.Combine(_root, "config");
        var config = new AppConfig
        {
            LibraryPath = Path.Combine(_root, "library"),
            Dark = true,
            Formats = ["Html", "Pdf"],
            Images = "Link"
        };
        config.Save(root);
        var loaded = AppConfig.Load(root);
        Assert.Equal(config.LibraryPath, loaded.LibraryPath);
        Assert.True(loaded.Dark);
        Assert.Equal("Html,Pdf", string.Join(",", loaded.Formats));
        Assert.Equal("Link", loaded.Images);
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));

        File.WriteAllText(Path.Combine(root, "config.json"), "{");
        Assert.Equal("", AppConfig.Load(root).LibraryPath);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch (IOException) { }
    }
}
