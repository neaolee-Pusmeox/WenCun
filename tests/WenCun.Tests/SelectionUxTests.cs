using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using WenCun.Core.Domain;
using WenCun.Core.Pending;
using WenCun.Core.Storage;
using WenCun.Desktop.ViewModels;

namespace WenCun.Tests;

public sealed class SelectionUxTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WenCunSelectionUxTests", Guid.NewGuid().ToString("N"));
    private readonly Library _library;
    private readonly PendingStore _store;
    private readonly TestActions _actions = new();
    private readonly List<string> _messages = [];

    public SelectionUxTests()
    {
        _library = new Library(Path.Combine(_root, "library"));
        _store = new PendingStore(_root);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LibraryHeaderTracksIndividualRows(bool pending)
    {
        var vm = LoadLibrary(pending, 2);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.AllSelected = true;
        changed.Clear();

        vm.Items[0].Selected = false;

        Assert.False(vm.AllSelected);
        Assert.Null(vm.SelectionState);
        Assert.Equal(1, vm.SelectedCount);
        Assert.Equal("当前页已选 1 篇", vm.SelectionText);
        Assert.Contains(nameof(vm.AllSelected), changed);
        Assert.Contains(nameof(vm.SelectionState), changed);
        Assert.Contains(nameof(vm.SelectedCount), changed);
        Assert.Contains(nameof(vm.SelectionText), changed);
        vm.Items[0].Selected = true;
        Assert.True(vm.AllSelected);
        Assert.True(vm.SelectionState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClearingPartialLibrarySelectionClearsEveryVisibleRow(bool pending)
    {
        var vm = LoadLibrary(pending, 2);
        vm.Items[0].Selected = true;
        vm.AllSelected = false;
        Assert.All(vm.Items, row => Assert.False(row.Selected));
    }

    [Fact]
    public void ClearingPartialPendingSelectionClearsEveryVisibleRow()
    {
        LoadLibrary(true, 2);
        var vm = NewPending();
        vm.Items[0].Selected = true;
        vm.AllSelected = false;
        Assert.All(vm.Items, row => Assert.False(row.Selected));
        Assert.Equal(0, vm.SelectedCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyLibraryCannotAppearFullySelected(bool pending)
    {
        var vm = LoadLibrary(pending, 0);
        vm.AllSelected = true;
        Assert.False(vm.AllSelected);
        Assert.False(vm.SelectionState);
        Assert.Equal(0, vm.SelectedCount);
        Assert.False(vm.ToggleSelectionCommand.CanExecute(null));
    }

    [Fact]
    public void EmptyPendingCannotAppearFullySelected()
    {
        var vm = NewPending();
        vm.AllSelected = true;
        Assert.False(vm.AllSelected);
        Assert.False(vm.SelectionState);
        Assert.False(vm.ToggleSelectionCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeHeaderClickSelectsAllFromPartialThenClears(bool pendingPage)
    {
        var library = LoadLibrary(pendingPage, 2);
        var pending = pendingPage ? NewPending() : null;
        object model = pendingPage ? pending! : library;
        RunSta(() =>
        {
            var header = new TestCheckBox { IsThreeState = true };
            header.SetBinding(ToggleButton.IsCheckedProperty, new Binding("SelectionState")
                { Source = model, Mode = BindingMode.OneWay });
            header.SetBinding(ButtonBase.CommandProperty, new Binding("ToggleSelectionCommand") { Source = model });
            if (pendingPage) pending!.Items[0].Selected = true;
            else library.Items[0].Selected = true;
            Assert.Null(header.IsChecked);
            header.SimulateClick();
            Assert.True(header.IsChecked);
            Assert.True(pendingPage ? pending!.AllSelected : library.AllSelected);
            header.SimulateClick();
            Assert.False(header.IsChecked);
            if (pendingPage) Assert.All(pending!.Items, row => Assert.False(row.Selected));
            else Assert.All(library.Items, row => Assert.False(row.Selected));
        });
    }

    [Fact]
    public void PendingRefreshDetachesRowsNoLongerDisplayed()
    {
        LoadLibrary(true, 2);
        var vm = NewPending();
        var oldRow = vm.Items[0];
        vm.Load();
        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        oldRow.Selected = true;
        Assert.Empty(changes);
        Assert.Equal(0, vm.SelectedCount);
    }

    [Fact]
    public void PendingRefreshKeepsOnlyExistingVisibleSelections()
    {
        LoadLibrary(true, 2);
        _store.Merge("other-biz", "Other Account", [Pending(0) with { Biz = "other-biz", Account = "Other Account" }], 1, true);
        var vm = NewPending();
        vm.AllSelected = true;
        vm.SelectedAccount = Assert.Single(vm.Accounts, row => row.Account.Biz == "fixture-biz");
        Assert.Equal(2, vm.SelectedCount);
        _store.Merge("fixture-biz", "Fixture Account", [Pending(2)], 3, true);
        vm.Load();
        Assert.Equal(2, vm.SelectedCount);
        Assert.Equal("当前页已选 2 篇", vm.SelectionText);
        Assert.Null(vm.SelectionState);
        Assert.False(vm.Items.Single(row => row.Article.Id == "article-2").Selected);
        vm.SelectionState = false;
        Assert.All(vm.Items, row => Assert.False(row.Selected));
        vm.Items[0].Selected = true;
        vm.SelectionState = null;
        Assert.True(vm.AllSelected);
        vm.SelectedAccount = null;
        Assert.Equal(3, vm.SelectedCount);
        Assert.False(vm.Items.Single(row => row.Article.Biz == "other-biz").Selected);
        vm.SaveCommand.Execute(null);
        Assert.Equal(3, _actions.Saved.Count);
        Assert.All(_actions.Saved, row => Assert.Equal("fixture-biz", row.Biz));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LibrarySelectionAndActionsStayOnTheVisiblePage(bool pending)
    {
        var vm = LoadLibrary(pending, 51);
        var oldRows = vm.Items.ToArray();
        vm.SelectionState = true;
        Assert.Equal(50, vm.SelectedCount);
        vm.NextCommand.Execute(null);
        Assert.Equal(0, vm.SelectedCount);
        Assert.False(vm.SelectionState);
        vm.SelectionState = null;
        Assert.True(vm.AllSelected);
        Assert.Equal("当前页已选 1 篇", vm.SelectionText);
        if (pending)
        {
            vm.SaveSelectedPendingCommand.Execute(null);
            Assert.Equal(Assert.Single(vm.Items).Pending, Assert.Single(_actions.Saved));
            vm.DeleteCommand.Execute(null);
            Assert.Single(_actions.Deleted);
        }
        else
        {
            vm.ReexportSelectedCommand.Execute(null);
            Assert.Equal(Assert.Single(vm.Items).Article, Assert.Single(_actions.Exported));
        }
        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        oldRows[0].Selected = false;
        Assert.Empty(changes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LibraryBulkCommandsRequireSelectionAndNotify(bool pending)
    {
        var vm = LoadLibrary(pending, 2);
        Assert.False(vm.DeleteCommand.CanExecute(null));
        Assert.False(vm.ReexportSelectedCommand.CanExecute(null));
        Assert.False(vm.SaveSelectedPendingCommand.CanExecute(null));
        var changes = 0;
        vm.DeleteCommand.CanExecuteChanged += (_, _) => changes++;
        vm.Items[0].Selected = true;
        Assert.True(vm.DeleteCommand.CanExecute(null));
        Assert.Equal(!pending, vm.ReexportSelectedCommand.CanExecute(null));
        Assert.Equal(pending, vm.SaveSelectedPendingCommand.CanExecute(null));
        Assert.True(changes > 0);
        changes = 0;
        vm.Load();
        Assert.False(vm.DeleteCommand.CanExecute(null));
        Assert.True(changes > 0);
    }

    [Fact]
    public void PendingBulkCommandsRequireSelectionAndNotify()
    {
        LoadLibrary(true, 2);
        var vm = NewPending();
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.False(vm.DeleteCommand.CanExecute(null));
        var changes = 0;
        vm.SaveCommand.CanExecuteChanged += (_, _) => changes++;
        vm.Items[0].Selected = true;
        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.True(vm.DeleteCommand.CanExecute(null));
        Assert.True(changes > 0);
        vm.AllSelected = false;
        Assert.False(vm.SaveCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PaginationDisablesBoundaryCommandsAndDropsPreviousPageSelection(bool pending)
    {
        var vm = LoadLibrary(pending, 51);
        Assert.False(vm.PrevCommand.CanExecute(null));
        Assert.True(vm.NextCommand.CanExecute(null));
        var changes = 0;
        vm.NextCommand.CanExecuteChanged += (_, _) => changes++;
        vm.AllSelected = true;
        vm.NextCommand.Execute(null);
        Assert.Equal("2 / 2", vm.PageText);
        Assert.False(Assert.Single(vm.Items).Selected);
        Assert.True(vm.PrevCommand.CanExecute(null));
        Assert.False(vm.NextCommand.CanExecute(null));
        Assert.True(changes > 0);
        vm.NextCommand.Execute(null);
        Assert.Equal("2 / 2", vm.PageText);
        vm.PrevCommand.Execute(null);
        Assert.Equal("1 / 2", vm.PageText);
        Assert.All(vm.Items, row => Assert.False(row.Selected));
        vm.PrevCommand.Execute(null);
        Assert.Equal("1 / 2", vm.PageText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RefreshAfterLastPageDisappearsReturnsToFirstPage(bool pending)
    {
        var vm = LoadLibrary(pending, 51);
        vm.NextCommand.Execute(null);
        var ids = Enumerable.Range(0, 51).Select(i => "article-" + i).ToArray();
        if (pending) _store.RemoveArticles("fixture-biz", ids);
        else _library.DeleteArticles(ids);
        vm.Load();
        Assert.Equal("1 / 1", vm.PageText);
        Assert.True(vm.IsEmpty);
        Assert.False(vm.PrevCommand.CanExecute(null));
        Assert.False(vm.NextCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(false, "   ")]
    [InlineData(true, "")]
    [InlineData(true, "   ")]
    public void ClearingSearchImmediatelyRestoresResults(bool pending, string cleared)
    {
        var vm = LoadLibrary(pending, 51);
        vm.Search = "no-such-article";
        vm.SearchCommand.Execute(null);
        Assert.True(vm.IsEmpty);
        vm.Search = cleared;
        Assert.Equal(50, vm.Items.Count);
        Assert.Equal("1 / 2", vm.PageText);
        Assert.All(vm.Items, row => Assert.False(row.Selected));
    }

    [Theory]
    [InlineData("fixture-author")]
    [InlineData("  FIXTURE-AUTHOR  ")]
    public void PendingSearchIncludesAuthorsAndTrimsInput(string search)
    {
        var vm = LoadLibrary(true, 2);
        vm.Search = search;
        vm.SearchCommand.Execute(null);
        Assert.Equal(2, vm.Items.Count);
    }

    [Fact]
    public void PendingSearchUsesTheDisplayedResolvedAccountName()
    {
        _library.EnsureAccount("fixture-biz", "Resolved Name");
        _store.Merge("fixture-biz", "", [Pending(0) with { Account = "" }], 1, true);
        var vm = new LibraryViewModel(_library, _store, _actions, _messages.Add) { ShowPending = true };
        Assert.Equal("Resolved Name", Assert.Single(vm.Items).Account);
        vm.Search = "resolved";
        vm.SearchCommand.Execute(null);
        Assert.Single(vm.Items);
    }

    [Fact]
    public void EmptyFailedFilterExplainsTheFilter()
    {
        var vm = LoadLibrary(false, 1);
        vm.ShowFailed = true;
        Assert.True(vm.IsEmpty);
        Assert.Contains("失败", vm.EmptyText);
        Assert.DoesNotContain("获取", vm.EmptyText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClearFiltersReturnsToAllAccountsAndSavedLibraryOnFirstPage(bool pending)
    {
        var vm = LoadLibrary(pending, 51);
        if (pending) _library.UpsertArticles([Article(100)]);
        vm.SelectedAccount = Assert.Single(vm.Accounts);
        vm.Search = "Fixture article";
        vm.SearchCommand.Execute(null);
        vm.NextCommand.Execute(null);
        vm.AllSelected = true;
        Assert.True(vm.CanClearFilters);
        Assert.True(vm.ClearFiltersCommand.CanExecute(null));
        var changes = new List<string?>();
        var commandChanges = 0;
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        vm.ClearFiltersCommand.CanExecuteChanged += (_, _) => commandChanges++;

        vm.ClearFiltersCommand.Execute(null);

        Assert.Equal("", vm.Search);
        Assert.Null(vm.SelectedAccount);
        Assert.True(vm.ShowAll);
        Assert.False(vm.IsPendingView);
        Assert.False(vm.CanClearFilters);
        Assert.False(vm.ClearFiltersCommand.CanExecute(null));
        Assert.StartsWith("1 / ", vm.PageText);
        Assert.Equal(0, vm.SelectedCount);
        Assert.All(vm.Items, row => Assert.False(row.Selected));
        Assert.All(vm.Items, row => Assert.False(row.IsPending));
        Assert.Contains(nameof(vm.Search), changes);
        Assert.Contains(nameof(vm.SelectedAccount), changes);
        Assert.Contains(nameof(vm.CanClearFilters), changes);
        Assert.Contains(nameof(vm.EmptyText), changes);
        Assert.True(commandChanges > 0);
        Assert.Equal(pending ? 1 : 50, vm.Items.Count);
    }

    [Fact]
    public void EmptyFeedbackAndClearActionDistinguishSearchStateAndEmptyLibrary()
    {
        var vm = LoadLibrary(false, 0);
        Assert.True(vm.IsEmpty);
        Assert.False(vm.CanClearFilters);
        var emptyLibraryText = vm.EmptyText;
        vm.Search = "missing";
        vm.SearchCommand.Execute(null);
        Assert.Contains("匹配", vm.EmptyText);
        Assert.True(vm.CanClearFilters);
        vm.ClearFiltersCommand.Execute(null);
        Assert.Equal(emptyLibraryText, vm.EmptyText);
        vm.ShowFailed = true;
        Assert.True(vm.CanClearFilters);
        Assert.Contains("失败", vm.EmptyText);
        vm.ClearFiltersCommand.Execute(null);
        Assert.True(vm.ShowAll);
        vm.ShowReady = true;
        Assert.Contains("已保存", vm.EmptyText);
        vm.ClearFiltersCommand.Execute(null);
        vm.ShowPending = true;
        Assert.Contains("待保存", vm.EmptyText);
        vm.ClearFiltersCommand.Execute(null);
        vm.Search = "   ";
        Assert.Equal(emptyLibraryText, vm.EmptyText);
        Assert.False(vm.CanClearFilters);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClearingSearchKeepsTheAccountAndStateFilters(bool pending)
    {
        var vm = LoadLibrary(pending, 2);
        vm.SelectedAccount = Assert.Single(vm.Accounts);
        if (!pending) vm.ShowReady = true;
        vm.Search = "missing";
        vm.SearchCommand.Execute(null);
        vm.Search = "";
        Assert.Equal(2, vm.Items.Count);
        Assert.NotNull(vm.SelectedAccount);
        Assert.True(vm.CanClearFilters);
        Assert.Equal(pending, vm.ShowPending);
        Assert.Equal(!pending, vm.ShowReady);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BusyActionsDisableCommandsAndStillDefendDirectExecution(bool pending)
    {
        var vm = LoadLibrary(pending, 1);
        vm.AllSelected = true;
        var changes = 0;
        vm.DeleteCommand.CanExecuteChanged += (_, _) => changes++;
        _actions.SetBusy(true);
        Assert.False(vm.DeleteCommand.CanExecute(null));
        Assert.False(vm.ReexportSelectedCommand.CanExecute(null));
        Assert.False(vm.SaveSelectedPendingCommand.CanExecute(null));
        Assert.True(changes > 0);
        vm.DeleteCommand.Execute(null);
        vm.ReexportSelectedCommand.Execute(null);
        vm.SaveSelectedPendingCommand.Execute(null);
        Assert.Equal(0, _actions.Calls);
        _actions.SetBusy(false);
        Assert.True(vm.DeleteCommand.CanExecute(null));
    }

    [Fact]
    public void DirectExecutionWithNoSelectionNeverCallsActionsOrOpensConfirmation()
    {
        var vm = LoadLibrary(false, 1);
        vm.DeleteCommand.Execute(null);
        vm.ReexportSelectedCommand.Execute(null);
        vm.SaveSelectedPendingCommand.Execute(null);
        var pending = NewPending();
        pending.SaveCommand.Execute(null);
        pending.DeleteCommand.Execute(null);
        Assert.Equal(0, _actions.Calls);
        Assert.Equal(5, _messages.Count);
        Assert.All(_messages, message => Assert.Contains("请先勾选", message));
    }

    private LibraryViewModel LoadLibrary(bool pending, int count)
    {
        if (pending && count > 0)
            _store.Merge("fixture-biz", "Fixture Account", Enumerable.Range(0, count).Select(Pending), count, true);
        else if (!pending)
            _library.UpsertArticles(Enumerable.Range(0, count).Select(Article));
        var vm = new LibraryViewModel(_library, _store, _actions, _messages.Add);
        if (pending) vm.ShowPending = true;
        else vm.Load();
        return vm;
    }

    private PendingViewModel NewPending()
    {
        var vm = new PendingViewModel(_store, _library.GetAccountName,
            rows => _ = _actions.SavePendingAsync(rows), _actions.DeletePending, _messages.Add);
        vm.Load();
        return vm;
    }

    private static Article Article(int index) => new()
    {
        Id = "article-" + index,
        Biz = "fixture-biz",
        Account = "Fixture Account",
        Title = "Fixture article " + index,
        Author = "fixture-author",
        Url = "https://example.invalid/article/" + index,
        PublishedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z").AddMinutes(index),
        State = ContentState.Ready
    };

    private static PendingArticle Pending(int index)
    {
        var article = Article(index);
        return new PendingArticle(article.Id, article.Biz, article.Account, article.Title, article.Author,
            article.Url, article.PublishedAt, article.PublishedAt!.Value);
    }

    private sealed class TestActions : ILibraryActions, INotifyPropertyChanged
    {
        public bool IsBusy { get; private set; }
        public int Calls { get; private set; }
        public IReadOnlyList<PendingArticle> Saved { get; private set; } = [];
        public IReadOnlyList<PendingArticle> Deleted { get; private set; } = [];
        public IReadOnlyList<Article> Exported { get; private set; } = [];
        public event PropertyChangedEventHandler? PropertyChanged;
        public void SetBusy(bool value)
        {
            IsBusy = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBusy)));
        }
        public void Read(Article article) => Calls++;
        public Task RetryAsync(Article article) { Calls++; return Task.CompletedTask; }
        public Task RetryAsync(IReadOnlyList<Article> articles) { Calls++; return Task.CompletedTask; }
        public Task ReexportAsync(IReadOnlyList<Article> articles) { Calls++; Exported = articles; return Task.CompletedTask; }
        public Task SavePendingAsync(IReadOnlyList<PendingArticle> articles) { Calls++; Saved = articles; return Task.CompletedTask; }
        public void DeletePending(IReadOnlyList<PendingArticle> articles) { Calls++; Deleted = articles; }
        public void CleanupLegacyPending() => Calls++;
        public void OpenFolder(Article article) => Calls++;
    }

    private sealed class TestCheckBox : CheckBox
    {
        public void SimulateClick() => OnClick();
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Selection binding test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
