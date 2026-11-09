using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using WenCun.Core.Domain;
using WenCun.Core.Pending;
using WenCun.Core.Storage;
using WenCun.Desktop.Infrastructure;

namespace WenCun.Desktop.ViewModels;

public sealed class LibraryViewModel : ObservableObject
{
    private const int PageSize = 50;
    private Library _library;
    private readonly PendingStore _pending;
    private readonly ILibraryActions _actions;
    private readonly Action<string> _status;
    private string _search = "";
    private AccountRow? _selectedAccount;
    private ContentState? _state;
    private bool _descending = true;
    private int _page = 1;
    private int _totalPages = 1;
    private int _total;
    private int _selectedCount;
    private bool _updatingSelection;
    private bool _loading;
    private bool _isPendingView;

    public LibraryViewModel(Library library, PendingStore pending, ILibraryActions actions, Action<string> status)
    {
        _library = library;
        _pending = pending;
        _actions = actions;
        _status = status;
        RefreshCommand = new RelayCommand(Load);
        SearchCommand = new RelayCommand(() => { _page = 1; LoadArticles(); });
        ClearFiltersCommand = new RelayCommand(ClearFilters, () => CanClearFilters);
        PrevCommand = new RelayCommand(() => { if (_page > 1) { _page--; LoadArticles(); } }, () => _page > 1);
        NextCommand = new RelayCommand(() => { if (_page < _totalPages) { _page++; LoadArticles(); } }, () => _page < _totalPages);
        // 表头单向绑定 SelectionState，用命令让半选点击变为全选。
        ToggleSelectionCommand = new RelayCommand(() => AllSelected = !AllSelected, () => Items.Count > 0);
        ToggleSortCommand = new RelayCommand(() =>
        {
            _descending = !_descending;
            OnPropertyChanged(nameof(SortText));
            _page = 1;
            LoadArticles();
        });
        SelectAllCommand = new RelayCommand(() => SelectedAccount = null);
        ReadCommand = new RelayCommand<ArticleRow>(row => _actions.Read(row.Article));
        RetryCommand = new RelayCommand<ArticleRow>(row => _ = _actions.RetryAsync(row.Article));
        OpenFolderCommand = new RelayCommand<ArticleRow>(row => _actions.OpenFolder(row.Article));
        SavePendingCommand = new RelayCommand<ArticleRow>(row =>
        {
            if (row.Pending is not null) _ = _actions.SavePendingAsync([row.Pending]);
        });
        SaveSelectedPendingCommand = new RelayCommand(() =>
        {
            if (RejectBusy()) return;
            var pending = Items.Where(row => row.Selected && row.Pending is not null).Select(row => row.Pending!).ToArray();
            if (pending.Length == 0) { _status("请先勾选要保存的文章。"); return; }
            _ = _actions.SavePendingAsync(pending);
        }, () => !_actions.IsBusy && _isPendingView && SelectedCount > 0);
        CleanupLegacyCommand = new RelayCommand(_actions.CleanupLegacyPending);
        RetryFailedCommand = new RelayCommand(() =>
        {
            if (_isPendingView) { _status("文章还没有正文，请先保存。"); return; }
            var failed = _library.FailedArticles(_selectedAccount?.Account.Biz);
            if (failed.Count == 0) { _status("当前范围没有采集失败的文章。"); return; }
            _ = _actions.RetryAsync(failed);
        });
        ReexportSelectedCommand = new RelayCommand(ReexportSelected, () => !_actions.IsBusy && !_isPendingView && SelectedCount > 0);
        DeleteCommand = new RelayCommand(DeleteSelected, () => !_actions.IsBusy && SelectedCount > 0);
        if (_actions is INotifyPropertyChanged observable)
            observable.PropertyChanged += (_, e) =>
            {
                if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(ILibraryActions.IsBusy))
                    NotifySelectionCommands();
            };
    }

    public ObservableCollection<AccountRow> Accounts { get; } = [];
    public ObservableCollection<ArticleRow> Items { get; } = [];

    public string Search
    {
        get => _search;
        set
        {
            if (!Set(ref _search, value ?? "")) return;
            NotifyFilters();
            if (string.IsNullOrWhiteSpace(_search) && !_loading)
            {
                _page = 1;
                LoadArticles();
            }
        }
    }

    public AccountRow? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (!Set(ref _selectedAccount, value)) return;
            _page = 1;
            OnPropertyChanged(nameof(IsAllAccounts));
            OnPropertyChanged(nameof(AccountFilterText));
            if (!_loading) LoadArticles();
        }
    }

    public bool IsAllAccounts => _selectedAccount is null;
    public string AccountFilterText => _selectedAccount is null ? "全部公众号" : _selectedAccount.Name;
    public string AllAccountsText => _isPendingView
        ? $"全部公众号（{Accounts.Sum(row => row.Account.Count)} 篇待保存）"
        : $"全部公众号（{Accounts.Sum(row => row.Account.Count)} 篇）";
    public bool ShowAll { get => !_isPendingView && _state is null; set { if (value) SetState(null); } }
    public bool ShowReady { get => !_isPendingView && _state == ContentState.Ready; set { if (value) SetState(ContentState.Ready); } }
    public bool ShowPending
    {
        get => _isPendingView;
        set
        {
            if (!value) return;
            _isPendingView = true;
            _state = null;
            _page = 1;
            OnPropertyChanged(nameof(ShowAll));
            OnPropertyChanged(nameof(ShowReady));
            OnPropertyChanged(nameof(ShowPending));
            OnPropertyChanged(nameof(ShowFailed));
            OnPropertyChanged(nameof(IsPendingView));
            Load();
        }
    }
    public bool ShowFailed { get => !_isPendingView && _state == ContentState.Failed; set { if (value) SetState(ContentState.Failed); } }
    public bool IsPendingView => _isPendingView;
    public string SortText => _descending ? "最新在前" : "最早在前";
    public string PageText => _page + " / " + Math.Max(1, _totalPages);
    public string Summary => _isPendingView
        ? $"{_total} 篇待保存" + (_selectedAccount is null ? "" : $"，{_selectedAccount.Name}")
        : $"{_total} 篇文章" + (_selectedAccount is null ? "" : $"，{_selectedAccount.Name}");
    public bool IsEmpty => Items.Count == 0;
    public bool CanClearFilters => !string.IsNullOrWhiteSpace(_search) || _selectedAccount is not null || _state is not null || _isPendingView;
    public string EmptyText => !string.IsNullOrWhiteSpace(_search)
        ? "没有匹配的文章。"
        : _isPendingView
            ? "没有待保存的文章。"
            : _state == ContentState.Failed
                ? "当前范围没有采集失败的文章。"
                : _state == ContentState.Ready
                    ? "当前范围没有已保存的文章。"
                    : _selectedAccount is not null
                        ? "这个公众号还没有文章。"
                        : "保存的文章会出现在这里。";

    public int SelectedCount => _selectedCount;
    public string SelectionText => $"当前页已选 {SelectedCount} 篇";
    public bool? SelectionState
    {
        get => SelectedCount == 0 ? false : SelectedCount == Items.Count ? true : null;
        set => AllSelected = value != false;
    }

    public bool AllSelected
    {
        get => Items.Count > 0 && SelectedCount == Items.Count;
        set
        {
            _updatingSelection = true;
            try { foreach (var item in Items) item.Selected = value; }
            finally { _updatingSelection = false; UpdateSelection(); }
        }
    }

    public ICommand RefreshCommand { get; }
    public ICommand SearchCommand { get; }
    public RelayCommand ClearFiltersCommand { get; }
    public RelayCommand PrevCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand ToggleSelectionCommand { get; }
    public ICommand ToggleSortCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand ReadCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand SavePendingCommand { get; }
    public RelayCommand SaveSelectedPendingCommand { get; }
    public ICommand CleanupLegacyCommand { get; }
    public ICommand RetryFailedCommand { get; }
    public RelayCommand ReexportSelectedCommand { get; }
    public RelayCommand DeleteCommand { get; }

    public void Load()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            LoadAccounts();
            LoadArticles();
        }
        finally { _loading = false; }
    }

    public void SetLibrary(Library library)
    {
        _library = library;
        _selectedAccount = null;
        SetState(null);
    }

    public void SelectAccount(string biz)
    {
        SetState(null);
        SelectedAccount = Accounts.FirstOrDefault(row => row.Account.Biz == biz);
    }

    private void LoadAccounts()
    {
        var biz = _selectedAccount?.Account.Biz;
        Accounts.Clear();
        if (_isPendingView)
        {
            foreach (var account in _pending.LoadAll())
            {
                var name = string.IsNullOrWhiteSpace(account.Account) ? _library.GetAccountName(account.Biz) ?? "" : account.Account;
                var synthetic = new Account(account.Biz, name, account.Articles.Count, 0, 0, 0, 0,
                    null, account.LastDiscovered, account.Complete);
                Accounts.Add(new AccountRow(synthetic, account.Articles.Count));
            }
        }
        else
        {
            foreach (var account in _library.Accounts()) Accounts.Add(new AccountRow(account));
        }
        _selectedAccount = biz is null ? null : Accounts.FirstOrDefault(row => row.Account.Biz == biz);
        OnPropertyChanged(nameof(SelectedAccount));
        OnPropertyChanged(nameof(IsAllAccounts));
        OnPropertyChanged(nameof(AccountFilterText));
        OnPropertyChanged(nameof(AllAccountsText));
    }

    private void LoadArticles()
    {
        if (_isPendingView)
        {
            LoadPendingArticles();
            return;
        }
        var result = _library.Query(new LibraryQuery(_search, _selectedAccount?.Account.Biz, _state, _page, PageSize, _descending));
        if (result.Items.Count == 0 && _page > 1)
        {
            _page = Math.Max(1, (int)Math.Ceiling(result.Total / (double)PageSize));
            if (result.Total > 0)
                result = _library.Query(new LibraryQuery(_search, _selectedAccount?.Account.Biz, _state, _page, PageSize, _descending));
        }
        SetItems(result.Items.Select(article => new ArticleRow(article)), result.Total);
    }

    private void LoadPendingArticles()
    {
        var search = _search.Trim();
        var rows = _pending.LoadAll()
            .Where(account => _selectedAccount is null || account.Biz == _selectedAccount.Account.Biz)
            .SelectMany(account =>
            {
                var name = string.IsNullOrWhiteSpace(account.Account) ? _library.GetAccountName(account.Biz) : account.Account;
                if (string.IsNullOrWhiteSpace(name)) name = account.Biz;
                return account.Articles.Select(article => article with
                {
                    Account = string.IsNullOrWhiteSpace(article.Account) ? name : article.Account
                });
            })
            .Where(article => search.Length == 0 ||
                              article.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                              article.Author.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                              article.Account.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var ordered = _descending
            ? rows.OrderByDescending(article => article.PublishedAt ?? DateTimeOffset.MinValue)
            : rows.OrderBy(article => article.PublishedAt ?? DateTimeOffset.MinValue);
        var list = ordered.ToArray();
        _page = Math.Min(_page, Math.Max(1, (int)Math.Ceiling(list.Length / (double)PageSize)));
        SetItems(list.Skip((_page - 1) * PageSize).Take(PageSize).Select(pending =>
            new ArticleRow(new Article
            {
                Id = pending.Id,
                Url = pending.Url,
                Biz = pending.Biz,
                Account = pending.Account,
                Title = pending.Title,
                Author = pending.Author,
                PublishedAt = pending.PublishedAt,
                State = ContentState.NotFetched,
                LastError = pending.LastError
            }, pending)), list.Length);
    }

    private void SetItems(IEnumerable<ArticleRow> rows, int total)
    {
        foreach (var item in Items) item.PropertyChanged -= OnRowChanged;
        Items.Clear();
        foreach (var item in rows)
        {
            item.PropertyChanged += OnRowChanged;
            Items.Add(item);
        }
        _total = total;
        _totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(IsEmpty));
        NotifyFilters();
        UpdateSelection();
        PrevCommand.RaiseCanExecuteChanged();
        NextCommand.RaiseCanExecuteChanged();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ArticleRow.Selected) && !_updatingSelection) UpdateSelection();
    }

    private void UpdateSelection()
    {
        _selectedCount = Items.Count(item => item.Selected);
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(SelectionState));
        OnPropertyChanged(nameof(AllSelected));
        ToggleSelectionCommand.RaiseCanExecuteChanged();
        NotifySelectionCommands();
    }

    private void NotifySelectionCommands()
    {
        DeleteCommand.RaiseCanExecuteChanged();
        ReexportSelectedCommand.RaiseCanExecuteChanged();
        SaveSelectedPendingCommand.RaiseCanExecuteChanged();
    }

    private void NotifyFilters()
    {
        OnPropertyChanged(nameof(CanClearFilters));
        OnPropertyChanged(nameof(EmptyText));
        ClearFiltersCommand.RaiseCanExecuteChanged();
    }

    private void ClearFilters()
    {
        Set(ref _search, "", nameof(Search));
        _selectedAccount = null;
        SetState(null);
    }

    private void SetState(ContentState? state)
    {
        _isPendingView = false;
        _state = state;
        _page = 1;
        OnPropertyChanged(nameof(ShowAll));
        OnPropertyChanged(nameof(ShowReady));
        OnPropertyChanged(nameof(ShowPending));
        OnPropertyChanged(nameof(ShowFailed));
        OnPropertyChanged(nameof(IsPendingView));
        Load();
    }

    private void ReexportSelected()
    {
        if (RejectBusy()) return;
        if (_isPendingView)
        {
            _status("文章还没有正文，保存后再导出。");
            return;
        }
        var selected = Items.Where(item => item.Selected).Select(item => item.Article).ToArray();
        if (selected.Length == 0)
        {
            _status("请先勾选要重新导出的文章。");
            return;
        }
        _ = _actions.ReexportAsync(selected);
    }

    private void DeleteSelected()
    {
        if (RejectBusy()) return;
        var selected = Items.Where(item => item.Selected).ToArray();
        if (selected.Length == 0)
        {
            _status("请先勾选要删除的文章。");
            return;
        }
        if (_isPendingView)
        {
            var pending = selected.Where(row => row.Pending is not null).Select(row => row.Pending!).ToArray();
            if (pending.Length == 0) return;
            _actions.DeletePending(pending);
            Load();
            return;
        }
        var answer = MessageBox.Show($"从资料库删除 {selected.Length} 篇文章？\n只删除资料库记录，已导出的文件保留。",
            "文存", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;
        try
        {
            _library.DeleteArticles(selected.Select(row => row.Article.Id));
            _status($"已删除 {selected.Length} 篇文章。");
            Load();
        }
        catch (Exception ex) when (ex is WenCunException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            _status("删除文章失败：" + ex.Message);
        }
    }

    private bool RejectBusy()
    {
        if (!_actions.IsBusy) return false;
        _status("正在采集、保存或导出，请等待当前任务结束。");
        return true;
    }
}
