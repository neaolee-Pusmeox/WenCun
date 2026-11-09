using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using WenCun.Core.Domain;
using WenCun.Core.Net;
using WenCun.Core.Pending;
using WenCun.Core.Storage;
using WenCun.Desktop.ViewModels;

namespace WenCun.Tests;

public sealed class WorkflowUxTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WenCunWorkflowTests", Guid.NewGuid().ToString("N"));
    private Library Library => new(Path.Combine(_root, "library"));
    private PendingStore Pending => new(_root);
    private static PendingArticle Item(string id = "a1", string biz = "a", string name = "公众号甲") =>
        new(id, biz, name, "文章" + id, "", "https://mp.weixin.qq.com/s/" + id,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"), DateTimeOffset.Parse("2026-01-02T00:00:00Z"));
    private static Article ArticleFor(PendingArticle item) => new()
    {
        Id = item.Id, Biz = item.Biz, Account = item.Account, Title = item.Title, Url = item.Url,
        PublishedAt = item.PublishedAt
    };
    private static Account AccountFor(string biz, string name) => new(biz, name, 0, 0, 0, 0, 0, null, null, false);

    private MainViewModel Model(HttpMessageHandler? handler = null)
    {
        var model = new MainViewModel(Library, _root, null,
            () => new WeChatClient(new HttpClient(handler ?? new FakeHttp(_ => throw new InvalidOperationException("Unexpected HTTP request.")))));
        model.Initialize();
        return model;
    }

    private void Seed(params PendingArticle[] items)
    {
        foreach (var group in items.GroupBy(item => item.Biz))
            Pending.Merge(group.Key, group.First().Account, group.ToArray(), 10, true);
    }

    [Fact]
    public void PendingOnlyStartupShowsTheExistingDirectory()
    {
        Seed(Item());
        var model = Model();
        Assert.Equal("Library", model.Section);
        Assert.True(model.LibraryPage.ShowPending);
        Assert.Equal("a1", Assert.Single(model.LibraryPage.Items).Article.Id);
        Assert.Empty(model.Library.Query(new LibraryQuery()).Items);
    }

    [Fact]
    public void StartupWithSavedArticlesKeepsTheLibraryView()
    {
        Seed(Item());
        Library.UpsertArticles([ArticleFor(Item("saved"))]);
        var model = Model();
        Assert.Equal("Library", model.Section);
        Assert.True(model.LibraryPage.ShowAll);
        Assert.Equal("saved", Assert.Single(model.LibraryPage.Items).Article.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingSaveWithoutMatchingSessionCarriesTargetAndExactSelection(bool anotherSession)
    {
        Seed(Item(), Item("a2"), Item("b1", "b", "公众号乙"));
        var model = Model();
        model.PendingPage.SelectAccount("b");
        model.PendingPage.AllSelected = true;
        if (anotherSession) SetField(model.CollectPage, "_session", SessionFor("b"));

        await model.SavePendingAsync([Item()]);

        Assert.Equal("Collect", model.Section);
        Assert.Equal("a", Field<string>(model.CollectPage, "_expectedBiz"));
        Assert.Contains("公众号甲", model.CollectPage.State);
        Assert.Equal("a", model.PendingPage.SelectedAccount?.Account.Biz);
        Assert.Equal("a1", Assert.Single(model.PendingPage.Items, row => row.Selected).Article.Id);
        Assert.Equal(2, model.PendingPage.Items.Count);
        Assert.Contains("1 篇", model.Status);
    }

    [Fact]
    public async Task MultipleAccountSelectionNamesTheNextTargetAndRemainingScope()
    {
        Seed(Item(), Item("a2"), Item("b1", "b", "公众号乙"));
        var model = Model();
        await model.SavePendingAsync([Item("b1", "b", "公众号乙"), Item(), Item("a2")]);
        Assert.Equal("b", Field<string>(model.CollectPage, "_expectedBiz"));
        Assert.Equal("b", model.PendingPage.SelectedAccount?.Account.Biz);
        Assert.Equal("b1", Assert.Single(model.PendingPage.Items, row => row.Selected).Article.Id);
        Assert.Contains("公众号乙", model.Status);
        Assert.Contains("1 篇", model.Status);
        Assert.Contains("2 篇", model.Status);
        Assert.Contains("分别", model.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryTargetComesFromArticlesInsteadOfStaleLibraryFilter(bool anotherSession)
    {
        var target = ArticleFor(Item());
        Library.UpsertArticles([target, ArticleFor(Item("b1", "b", "公众号乙"))]);
        var model = Model();
        model.LibraryPage.SelectAccount("b");
        if (anotherSession) SetField(model.CollectPage, "_session", SessionFor("b"));

        await model.RetryAsync(target);

        Assert.Equal("Collect", model.Section);
        Assert.Equal("a", Field<string>(model.CollectPage, "_expectedBiz"));
        Assert.Contains("公众号甲", model.CollectPage.State);
        Assert.Contains("1 篇", model.Status);
        Assert.Contains("重试", model.Status);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("failed")]
    [InlineData("all")]
    public async Task SavedBodyIsVisibleDespiteOldStateSearchAndAccountFilters(string filter)
    {
        Seed(Item(), Item("b1", "b", "公众号乙"));
        Library.UpsertArticles([ArticleFor(Item("old", "b", "公众号乙"))]);
        var model = Model(new FakeHttp(_ => Task.FromResult(BodyResponse())));
        SetField(model.CollectPage, "_session", SessionFor("a"));
        if (filter == "pending") model.LibraryPage.ShowPending = true;
        if (filter == "failed") model.LibraryPage.ShowFailed = true;
        model.LibraryPage.SelectedAccount = model.LibraryPage.Accounts.Single(row => row.Account.Biz == "b");
        model.LibraryPage.Search = "will-not-match";
        model.LibraryPage.SearchCommand.Execute(null);
        model.Section = "Collect";

        await model.SavePendingAsync([Item()]);

        Assert.NotNull(model.Library.GetContent("a1"));
        Assert.Equal("Library", model.Section);
        Assert.True(model.LibraryPage.ShowAll);
        Assert.Equal("", model.LibraryPage.Search);
        Assert.Contains(model.LibraryPage.Items, row => row.Article.Id == "a1");
        Assert.DoesNotContain(Pending.LoadAll().SelectMany(account => account.Articles), item => item.Id == "a1");
        Assert.Contains(Pending.LoadAll().SelectMany(account => account.Articles), item => item.Id == "b1");
    }

    [Fact]
    public async Task SuccessfulRetryLeavesFailedFilterAndShowsItsBody()
    {
        var article = ArticleFor(Item());
        Library.UpsertArticles([article]);
        Library.SetFailure(article.Id, "模拟失败");
        var model = Model(new FakeHttp(_ => Task.FromResult(BodyResponse())));
        SetField(model.CollectPage, "_session", SessionFor("a"));
        model.LibraryPage.ShowFailed = true;
        await model.RetryAsync(article);
        Assert.NotNull(model.Library.GetContent(article.Id));
        Assert.True(model.LibraryPage.ShowAll);
        Assert.Contains(model.LibraryPage.Items, row => row.Article.Id == article.Id);
    }

    [Fact]
    public async Task FailedSaveKeepsPendingFilterAndSelectionContext()
    {
        Seed(Item());
        var model = Model(new FakeHttp(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        SetField(model.CollectPage, "_session", SessionFor("a"));
        model.LibraryPage.ShowPending = true;
        await model.SavePendingAsync([Item()]);
        Assert.True(model.LibraryPage.ShowPending);
        Assert.Null(model.Library.GetContent("a1"));
        Assert.Single(Pending.Load("a")!.Articles);
    }

    [Fact]
    public void InitialInstructionsPutStartBeforeOpeningWeChat()
    {
        var detail = Model().CollectPage.Detail;
        var start = detail.IndexOf("获取目录", StringComparison.Ordinal);
        var wechat = detail.IndexOf("微信", StringComparison.Ordinal);
        Assert.True(start >= 0 && wechat > start, detail);
    }

    [Fact]
    public void ConsentRequeriesStartAvailability()
    {
        var collect = Model().CollectPage;
        Assert.False(collect.StartCommand.CanExecute(null));
        var notifications = 0;
        collect.StartCommand.CanExecuteChanged += (_, _) => notifications++;
        collect.Consent = true;
        Assert.True(collect.StartCommand.CanExecute(null));
        collect.Consent = false;
        Assert.False(collect.StartCommand.CanExecute(null));
        Assert.True(notifications >= 2);
    }

    [Fact]
    public void NativeStartButtonTracksConsentAndTheSharedWorkLock()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var model = Model();
                var button = new Button { DataContext = model.CollectPage };
                button.SetBinding(Button.CommandProperty, new Binding(nameof(CollectViewModel.StartCommand)));
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                Assert.False(button.IsEnabled);
                model.CollectPage.Consent = true;
                Assert.True(button.IsEnabled);
                var begin = typeof(MainViewModel).GetMethod("TryBeginWork", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var end = typeof(MainViewModel).GetMethod("EndWork", BindingFlags.Instance | BindingFlags.NonPublic)!;
                Assert.True((bool)begin.Invoke(model, null)!);
                try { Assert.False(button.IsEnabled); }
                finally { end.Invoke(model, null); }
                Assert.True(button.IsEnabled);
            }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Native command binding timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public async Task StartExecutionStillDefendsConsentWithoutStartingAProxy()
    {
        var collect = Model().CollectPage;
        var start = typeof(CollectViewModel).GetMethod("StartAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)start.Invoke(collect, null)!;
        Assert.Equal("请先同意证书和代理设置", collect.State);
        Assert.False(collect.IsBusy);
        Assert.Null(Field<CancellationTokenSource>(collect, "_cts"));
    }

    [Fact]
    public void PendingPageSaveCarriesItsCurrentAccountAndCheckedRows()
    {
        Seed(Item(), Item("a2"), Item("b1", "b", "公众号乙"));
        var model = Model();
        model.PendingPage.SelectAccount("a");
        model.PendingPage.Items.Single(row => row.Article.Id == "a2").Selected = true;
        model.PendingPage.SaveCommand.Execute(null);
        Assert.Equal("Collect", model.Section);
        Assert.Equal("a", Field<string>(model.CollectPage, "_expectedBiz"));
        Assert.Equal("a2", Assert.Single(model.PendingPage.Items, row => row.Selected).Article.Id);
    }

    [Fact]
    public async Task SaveDisablesSettingsAndStartAndNotifiesOnCompletion()
    {
        Seed(Item());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = Model(new FakeHttp(async _ => { entered.SetResult(); await release.Task; return BodyResponse(); }));
        SetField(model.CollectPage, "_session", SessionFor("a"));
        model.CollectPage.Consent = true;
        var settings = new List<bool>();
        var start = new List<bool>();
        model.CollectPage.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CollectViewModel.CanEditSettings)) settings.Add(model.CollectPage.CanEditSettings);
        };
        model.CollectPage.StartCommand.CanExecuteChanged += (_, _) => start.Add(model.CollectPage.StartCommand.CanExecute(null));

        var saving = model.SavePendingAsync([Item()]);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(model.IsBusy);
            Assert.False(model.CollectPage.CanEditSettings);
            Assert.False(model.CollectPage.StartCommand.CanExecute(null));
            Assert.Contains(false, settings);
            Assert.Contains(false, start);
        }
        finally { release.TrySetResult(); await saving; }

        Assert.False(model.IsBusy);
        Assert.True(model.CollectPage.CanEditSettings);
        Assert.True(model.CollectPage.StartCommand.CanExecute(null));
        Assert.True(settings.Last());
        Assert.True(start.Last());
    }

    [Fact]
    public async Task ExportDisablesSettingsAndNotifiesBothTransitions()
    {
        var article = ArticleFor(Item());
        Library.UpsertArticles([article]);
        Library.SaveContent(new(article.Id, "<p>临时正文</p>", DateTimeOffset.UtcNow, [], []), ContentState.Ready);
        var model = Model();
        var observed = new List<bool>();
        model.CollectPage.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CollectViewModel.CanEditSettings)) observed.Add(model.CollectPage.CanEditSettings);
        };
        await model.ReexportAsync([article]);
        Assert.Equal(new[] { false, true }, observed);
    }

    [Fact]
    public void CannotRetargetAnActiveCollection()
    {
        var model = Model();
        model.CollectPage.SetTarget(AccountFor("a", "公众号甲"));
        SetField(model.CollectPage, "_busy", true);
        model.CollectPage.SetTarget(AccountFor("b", "公众号乙"));
        Assert.Equal("a", Field<string>(model.CollectPage, "_expectedBiz"));
        Assert.Contains("公众号甲", model.CollectPage.State);
        Assert.Contains("结束", model.CollectPage.Detail);
    }

    [Fact]
    public void AccountActionCannotRetargetOrNavigateDuringCollection()
    {
        Seed(Item(), Item("b1", "b", "公众号乙"));
        var model = Model();
        model.CollectPage.SetTarget(AccountFor("a", "公众号甲"));
        SetField(model.CollectPage, "_busy", true);
        model.Section = "Accounts";
        model.AccountsPage.CollectCommand.Execute(model.AccountsPage.Items.Single(row => row.Account.Biz == "b"));
        Assert.Equal("a", Field<string>(model.CollectPage, "_expectedBiz"));
        Assert.Equal("Accounts", model.Section);
        Assert.Contains("结束", model.Status);
    }

    [Fact]
    public async Task BusySaveWithoutSessionDoesNotChangeCollectionContext()
    {
        Seed(Item());
        var model = Model();
        model.CollectPage.SetTarget(AccountFor("b", "公众号乙"));
        SetField(model.CollectPage, "_busy", true);
        model.Section = "Library";
        await model.SavePendingAsync([Item()]);
        Assert.Equal("Library", model.Section);
        Assert.Equal("b", Field<string>(model.CollectPage, "_expectedBiz"));
        Assert.Contains("结束", model.Status);
    }

    [Fact]
    public async Task StopShowsCleanupInProgressUntilTheCompletionSignal()
    {
        var collect = Model().CollectPage;
        using var cancellation = new CancellationTokenSource();
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SetField(collect, "_busy", true);
        SetField(collect, "_cts", cancellation);
        SetField(collect, "_stopped", cleanup);

        collect.StopCommand.Execute(null);
        var stopped = collect.StopAsync();
        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(collect.IsBusy);
        Assert.False(stopped.IsCompleted);
        Assert.Contains("正在停止", collect.State);
        Assert.Contains("清理", collect.Detail);
        Assert.False(collect.StopCommand.CanExecute(null));
        cleanup.SetResult();
        await stopped;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeletingPendingWithoutConfirmationPreservesTheExactManifest(bool subscribe)
    {
        Seed(Item(), Item("a2"));
        var model = Model();
        var before = Manifest();
        if (subscribe) model.ConfirmPendingDeleteRequested += _ => false;
        model.DeletePending([Item()]);
        Assert.Equal(before, Manifest());
        Assert.Equal(2, Pending.Load("a")!.Articles.Count);
    }

    [Fact]
    public void ConfirmedDeletionRemovesOnlySelectedEntriesAndReportsActualCount()
    {
        Seed(Item(), Item("a2"), Item("b1", "b", "公众号乙"));
        var article = ArticleFor(Item());
        Library.UpsertArticles([article]);
        Library.SaveContent(new(article.Id, "<p>已保存正文</p>", DateTimeOffset.UtcNow, [], []), ContentState.Ready);
        var exported = Path.Combine(_root, "library", "articles", "fixture.html");
        Directory.CreateDirectory(Path.GetDirectoryName(exported)!);
        File.WriteAllText(exported, "导出文件");
        var model = Model();
        var confirmed = 0;
        model.ConfirmPendingDeleteRequested += count => { confirmed = count; return true; };

        model.DeletePending([Item(), Item(), Item("missing")]);

        Assert.Equal(1, confirmed);
        Assert.Equal("a2", Assert.Single(Pending.Load("a")!.Articles).Id);
        Assert.Single(Pending.Load("b")!.Articles);
        Assert.NotNull(model.Library.GetContent("a1"));
        Assert.Equal("导出文件", File.ReadAllText(exported));
        Assert.Contains("已移除", model.Status);
        Assert.Contains("1 篇", model.Status);
    }

    [Fact]
    public void BusyDeletionNeverRequestsConfirmation()
    {
        Seed(Item());
        var model = Model();
        var requests = 0;
        model.ConfirmPendingDeleteRequested += _ => { requests++; return true; };
        SetField(model.CollectPage, "_busy", true);
        model.DeletePending([Item()]);
        Assert.Equal(0, requests);
        Assert.Single(Pending.Load("a")!.Articles);
        Assert.Contains("结束", model.Status);
    }

    [Fact]
    public void TaskStartedWhileConfirmationIsOpenPreventsDeletion()
    {
        Seed(Item());
        var model = Model();
        var requests = 0;
        var before = Manifest();
        model.ConfirmPendingDeleteRequested += _ =>
        {
            requests++;
            SetField(model.CollectPage, "_busy", true);
            return true;
        };
        model.DeletePending([Item()]);
        Assert.Equal(1, requests);
        Assert.Equal(before, Manifest());
        Assert.Contains("结束", model.Status);
    }

    private string Manifest() => string.Join("\n", Directory.GetFiles(Pending.Directory, "*.json")
        .OrderBy(path => path, StringComparer.Ordinal).Select(File.ReadAllText));
    private static Session SessionFor(string biz) => new() { Biz = biz, Cookie = "", UserAgent = "WorkflowFixture" };
    private static HttpResponseMessage BodyResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("<html><h1 id='activity-name'>假数据文章</h1><div id='js_content'><p>用于工作流回归测试的正文。</p></div></html>")
    };
    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static T? Field<T>(object target, string name) =>
        (T?)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private sealed class FakeHttp(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }

    public void Dispose()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(Field<string>(Library, "_connectionString"));
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
