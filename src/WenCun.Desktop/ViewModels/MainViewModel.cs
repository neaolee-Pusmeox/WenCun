using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using WenCun.Core.Collect;
using WenCun.Core.Content;
using WenCun.Core.Domain;
using WenCun.Core.Export;
using WenCun.Core.Net;
using WenCun.Core.Pending;
using WenCun.Core.Storage;
using WenCun.Desktop.Infrastructure;

namespace WenCun.Desktop.ViewModels;

public sealed class MainViewModel : ObservableObject, ILibraryActions
{
    private Library _library;
    private readonly string _appRoot;
    private readonly SemaphoreSlim _workGate = new(1, 1);
    private string _section = "Library";
    private string _status = "就绪";

    public MainViewModel(Library library, string appRoot, string? recoveryWarning)
    {
        _library = library;
        _appRoot = appRoot;
        Pending = new PendingStore(appRoot);
        PdfRenderer = new WebViewPdfRenderer(appRoot);
        LibraryPage = new LibraryViewModel(library, Pending, this, SetStatus);
        PendingPage = new PendingViewModel(Pending, biz => _library.GetAccountName(biz),
            selected => _ = SavePendingAsync(selected), DeletePending, SetStatus);
        CollectPage = new CollectViewModel(library, appRoot, PdfRenderer, Pending, OnDiscovered, () => PendingPage.Load(), () => _workGate.CurrentCount > 0);
        AccountsPage = new AccountsViewModel(library, Pending, ViewAccount, CollectAccount, RetryFailedAsync,
            ReexportFailedAsync, DeleteAccountAsync, ViewPending);
        _section = _library.Accounts().Count == 0 && !Pending.LoadAll().Any() ? "Collect" : "Library";
        CollectPage.ChangeLibraryRequested += () => ChangeLibraryRequested?.Invoke();
        CollectPage.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CollectViewModel.IsBusy)) OnPropertyChanged(nameof(IsBusy));
        };
        OpenFolderCommand = new RelayCommand(() => OpenPath(_library.RootDirectory));
        GoToCollectCommand = new RelayCommand(() => Section = "Collect");
        ToggleThemeCommand = new RelayCommand(() => App.SetTheme(!App.Config.Dark));
        if (!string.IsNullOrWhiteSpace(recoveryWarning)) _status = recoveryWarning;
    }

    public Library Library => _library;
    public bool IsBusy => CollectPage.IsBusy || _workGate.CurrentCount == 0;
    public PendingStore Pending { get; }
    public IPdfRenderer PdfRenderer { get; }
    public LibraryViewModel LibraryPage { get; }
    public CollectViewModel CollectPage { get; }
    public PendingViewModel PendingPage { get; }
    public AccountsViewModel AccountsPage { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand GoToCollectCommand { get; }
    public ICommand ToggleThemeCommand { get; }

    public event Action<Article, ArticleContent>? ReadRequested;
    public event Action? ChangeLibraryRequested;

    public string Section
    {
        get => _section;
        set
        {
            if (!Set(ref _section, value)) return;
            OnPropertyChanged(nameof(IsLibrarySection));
            OnPropertyChanged(nameof(IsCollectSection));
            OnPropertyChanged(nameof(IsAccountsSection));
            OnPropertyChanged(nameof(SectionTitle));
            OnPropertyChanged(nameof(SectionSubtitle));
            if (value == "Library") LibraryPage.Load();
            if (value == "Accounts") AccountsPage.Load();
        }
    }

    public bool IsLibrarySection => _section == "Library";
    public bool IsCollectSection => _section == "Collect";
    public bool IsAccountsSection => _section == "Accounts";
    public string SectionTitle => _section switch
    {
        "Collect" => "采集",
        "Accounts" => "公众号",
        _ => "资料库"
    };
    public string SectionSubtitle => _section switch
    {
        "Collect" => "选择公众号文章，保存到本地",
        "Accounts" => "查看各公众号的保存进度",
        _ => "查找、阅读和导出文章"
    };
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string AppRoot => _appRoot;
    public string LibraryPath => _library.RootDirectory;
    public string BuildText => $"版本 {App.Version}";

    public void Initialize()
    {
        LibraryPage.Load();
        PendingPage.Load();
        AccountsPage.Load();
    }

    public void SetStatus(string status) => Status = status;

    public void ChangeLibrary(string path)
    {
        if (IsBusy)
        {
            SetStatus("采集、保存或导出进行中，结束后再切换文库。");
            return;
        }
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (string.Equals(full, _library.RootDirectory, StringComparison.OrdinalIgnoreCase)) return;
        if (File.Exists(Path.Combine(full, "library.db")))
        {
            var old = _library.RootDirectory;
            SwitchLibrary(full);
            if (string.Equals(_library.RootDirectory, full, StringComparison.OrdinalIgnoreCase))
                SetStatus($"已打开另一个文库；原文库保留在 {old}。");
            return;
        }
        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
        {
            SetStatus("目标文件夹不是空文件夹，也不包含文库。");
            return;
        }
        try { _library.CopyTo(full); }
        catch (Exception ex) when (IsExpected(ex))
        {
            SetStatus("移动文库失败：" + ex.Message);
            return;
        }
        var previous = _library.RootDirectory;
        SwitchLibrary(full);
        if (string.Equals(_library.RootDirectory, full, StringComparison.OrdinalIgnoreCase))
            SetStatus($"已复制文库到 {full}；旧文库保留在 {previous}，确认后可手动删除。");
    }

    public void SwitchLibrary(string path)
    {
        if (IsBusy)
        {
            SetStatus("采集、保存或导出进行中，结束后再切换文库。");
            return;
        }
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (string.Equals(full, _library.RootDirectory, StringComparison.OrdinalIgnoreCase)) return;
        try { _library = new Library(full); }
        catch (Exception ex)
        {
            SetStatus("打开文库失败：" + ex.Message);
            return;
        }
        App.Config.LibraryPath = _library.RootDirectory;
        App.Config.Save(_appRoot);
        LibraryPage.SetLibrary(_library);
        CollectPage.SetLibrary(_library);
        AccountsPage.SetLibrary(_library);
        PendingPage.Load();
        OnPropertyChanged(nameof(LibraryPath));
    }

    public void Read(Article article)
    {
        var content = _library.GetContent(article.Id);
        if (content is null)
        {
            SetStatus("这篇还没有正文，请先保存。");
            return;
        }
        ReadRequested?.Invoke(article, content);
    }

    public Task RetryAsync(Article article) => RetryAsync([article]);

    public async Task RetryAsync(IReadOnlyList<Article> articles)
    {
        if (articles.Count == 0) return;
        var session = CollectPage.LastSession;
        if (session is null)
        {
            Section = "Collect";
            if (LibraryPage.SelectedAccount is { } selected) CollectPage.SetTarget(selected.Account);
            SetStatus("请先在采集页获取一次目录，再重试。");
            return;
        }
        var eligible = articles.Where(article => article.Biz == session.Biz).ToArray();
        if (eligible.Length == 0)
        {
            SetStatus("这些文章属于另一个公众号，请先获取该公众号的目录。");
            return;
        }
        if (!TryBeginWork()) return;
        try
        {
            SetStatus($"正在重试 {eligible.Length} 篇…");
            using var client = new WeChatClient();
            var collector = new Collector(_library, client, Pending, App.Config.ToSaveOptions(), PdfRenderer);
            var done = 0;
            foreach (var article in eligible)
            {
                try
                {
                    await collector.FetchBodyAsync(article, session, CancellationToken.None);
                    try { Pending.RemoveArticles(article.Biz, [article.Id]); }
                    catch (WenCunException) { /* Corrupt pending stays untouched; the saved article is still valid. */ }
                    done++;
                }
                catch (Exception ex) when (IsExpected(ex))
                {
                    _library.SetFailure(article.Id, ex.Message);
                }
            }
            var skipped = articles.Count - eligible.Length;
            SetStatus(skipped == 0
                ? $"重试完成：成功 {done}，失败 {eligible.Length - done}。"
                : $"重试完成：成功 {done}，失败 {eligible.Length - done}，跳过 {skipped} 篇其他公众号。");
        }
        finally { EndWork(); }
        LibraryPage.Load();
        AccountsPage.Load();
    }

    public async Task ReexportAsync(IReadOnlyList<Article> articles)
    {
        if (articles.Count == 0) return;
        var options = App.Config.ToSaveOptions();
        if (options.Formats.Count == 0)
        {
            SetStatus("请先在采集页选择至少一种文件格式。");
            return;
        }
        if (!TryBeginWork()) return;
        try
        {
            SetStatus($"正在重新导出 {articles.Count} 篇…");
            var exporter = new Exporter(_library, PdfRenderer);
            var result = await exporter.ExportAsync(
                new ExportRequest(articles, Path.Combine(_library.RootDirectory, "articles"), options.Formats),
                null, CancellationToken.None);
            foreach (var group in result.Items.GroupBy(item => item.ArticleId))
            {
                var failed = group.Where(item => !item.Ok).ToArray();
                _library.SetExportResult(group.Key,
                    failed.Length == 0 ? ExportState.Succeeded : ExportState.Failed,
                    failed.Length == 0 ? null : string.Join("；", failed.Select(item => item.Format + "：" + item.Error)));
            }
            SetStatus($"重新导出完成：成功 {result.Succeeded}，失败 {result.Failed}。");
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            SetStatus("重新导出失败：" + ex.Message);
        }
        finally { EndWork(); }
        LibraryPage.Load();
        AccountsPage.Load();
    }

    public void OpenFolder(Article article)
    {
        var folder = ExportPaths.FolderFor(Path.Combine(_library.RootDirectory, "articles"), article);
        if (!Directory.Exists(folder))
        {
            var oldArticleFolder = ExportPaths.LegacyFolderFor(Path.Combine(_library.RootDirectory, "articles"), article);
            if (Directory.Exists(oldArticleFolder))
            {
                OpenPath(oldArticleFolder);
                return;
            }
            var legacy = Path.Combine(_library.RootDirectory,
                UrlRules.SafeFileName(article.Account.Length > 0 ? article.Account : "未命名公众号"));
            if (Directory.Exists(legacy))
            {
                OpenPath(legacy);
                return;
            }
            SetStatus("这篇文章还没有导出文件，先保存或重新导出。");
            return;
        }
        OpenPath(folder);
    }

    private void OnDiscovered()
    {
        PendingPage.Load();
        LibraryPage.Load();
        AccountsPage.Load();
        SetStatus("目录已获取，勾选文章后保存。");
    }

    public async Task SavePendingAsync(IReadOnlyList<PendingArticle> selected)
    {
        if (selected.Count == 0) return;
        var session = CollectPage.LastSession;
        if (session is null)
        {
            Section = "Collect";
            SetStatus("请先获取这个公众号的目录，再保存文章。");
            return;
        }
        var eligible = selected.Where(article => string.Equals(article.Biz, session.Biz, StringComparison.Ordinal)).ToArray();
        if (eligible.Length == 0)
        {
            Section = "Collect";
            SetStatus("这些文章属于另一个公众号，请先获取该公众号的目录。");
            return;
        }
        if (!TryBeginWork()) return;
        var saved = 0;
        try
        {
            SetStatus($"正在保存 {eligible.Length} 篇…");
            using var client = new WeChatClient();
            var collector = new Collector(_library, client, Pending, App.Config.ToSaveOptions(), PdfRenderer);
            var progress = new Progress<CollectProgress>(report =>
                SetStatus($"正在保存 {Math.Min(report.Saved + 1, report.Found)}/{report.Found} 篇：{report.Detail}"));
            foreach (var group in eligible.GroupBy(article => article.Biz))
                saved += await collector.SaveSelectedAsync(session, group.Key, group.ToArray(), progress, CancellationToken.None);
            var skipped = selected.Count - eligible.Length;
            SetStatus(skipped == 0
                ? $"保存完成：{saved} 篇成功，{eligible.Length - saved} 篇失败。"
                : $"保存完成：{saved} 篇成功，{eligible.Length - saved} 篇失败，{skipped} 篇属于其他公众号。");
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            SetStatus("保存失败：" + ex.Message);
        }
        finally { EndWork(); }
        PendingPage.Load();
        LibraryPage.Load();
        AccountsPage.Load();
        if (saved > 0) Section = "Library";
    }

    public void DeletePending(IReadOnlyList<PendingArticle> selected)
    {
        if (RejectBusy()) return;
        try
        {
            foreach (var group in selected.GroupBy(article => article.Biz))
                Pending.RemoveArticles(group.Key, group.Select(article => article.Id));
            LibraryPage.Load();
            PendingPage.Load();
            AccountsPage.Load();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            SetStatus("删除待保存记录失败：" + ex.Message);
        }
    }

    public void CleanupLegacyPending()
    {
        if (RejectBusy()) return;
        try
        {
            var count = _library.DeleteLegacyPending();
            SetStatus(count == 0 ? "没有旧记录。" : $"已清理 {count} 条旧记录。");
            LibraryPage.Load();
            AccountsPage.Load();
            PendingPage.Load();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            SetStatus("清理旧记录失败：" + ex.Message);
        }
    }

    private Task DeleteAccountAsync(Account account)
    {
        if (RejectBusy()) return Task.CompletedTask;
        var answer = MessageBox.Show(
            $"删除公众号“{account.Name}”？\n将删除其中的 {account.Count} 篇文章、正文、图片、导出文件和待保存清单，无法撤销。",
            "文存", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return Task.CompletedTask;
        try
        {
            var (articles, _, _) = _library.DeleteAccountData(account.Biz);
            Pending.RemoveAccount(account.Biz);
            SetStatus($"已删除公众号「{account.Name}」：{articles} 篇文章。");
            LibraryPage.Load();
            AccountsPage.Load();
            PendingPage.Load();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            SetStatus("删除公众号失败：" + ex.Message);
        }
        return Task.CompletedTask;
    }

    private void ViewAccount(Account account)
    {
        Section = "Library";
        LibraryPage.SelectAccount(account.Biz);
    }

    private void CollectAccount(Account account)
    {
        Section = "Collect";
        CollectPage.SetTarget(account);
    }

    private void ViewPending(Account account)
    {
        Section = "Collect";
        PendingPage.SelectAccount(account.Biz);
    }

    private async Task RetryFailedAsync(Account account)
    {
        var articles = _library.FailedArticles(account.Biz);
        if (articles.Count == 0)
        {
            SetStatus("这个公众号没有采集失败的文章。");
            return;
        }
        if (CollectPage.LastSession is null) CollectPage.SetTarget(account);
        await RetryAsync(articles);
    }

    private async Task ReexportFailedAsync(Account account)
    {
        var articles = _library.ExportFailedArticles(account.Biz);
        if (articles.Count == 0)
        {
            SetStatus("这个公众号没有导出失败的文章。");
            return;
        }
        await ReexportAsync(articles);
    }

    private void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { SetStatus("打开文件夹失败：" + ex.Message); }
    }

    private bool RejectBusy()
    {
        if (!IsBusy) return false;
        SetStatus("正在采集、保存或导出，请等待当前任务结束。");
        return true;
    }

    private static bool IsExpected(Exception ex) =>
        ex is WenCunException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException;

    private bool TryBeginWork()
    {
        if (RejectBusy()) return false;
        _workGate.Wait(0);
        OnPropertyChanged(nameof(IsBusy));
        return true;
    }

    private void EndWork()
    {
        _workGate.Release();
        OnPropertyChanged(nameof(IsBusy));
    }
}
