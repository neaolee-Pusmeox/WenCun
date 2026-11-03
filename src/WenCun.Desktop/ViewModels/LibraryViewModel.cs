using System.Collections.ObjectModel;
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
    private bool _allSelected;
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
        PrevCommand = new RelayCommand(() => { if (_page > 1) { _page--; LoadArticles(); } });
        NextCommand = new RelayCommand(() => { if (_page < _totalPages) { _page++; LoadArticles(); } });
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
            var pending = Items.Where(row => row.Selected && row.Pending is not null).Select(row => row.Pending!).ToArray();
            if (pending.Length == 0) { _status("请先勾选要保存的待采集文章。"); return; }
            _ = _actions.SavePendingAsync(pending);
        });
        CleanupLegacyCommand = new RelayCommand(_actions.CleanupLegacyPending);
        RetryFailedCommand = new RelayCommand(() =>
        {
            if (_isPendingView) { _status("待采集文章还没有正文，先保存。"); return; }
            var failed = _library.FailedArticles(_selectedAccount?.Account.Biz);
            if (failed.Count == 0) { _status("当前范围没有采集失败的文章。"); return; }
            _ = _actions.RetryAsync(failed);
        });
        ReexportSelectedCommand = new RelayCommand(ReexportSelected);
        DeleteCommand = new RelayCommand(DeleteSelected);
    }

    public ObservableCollection<AccountRow> Accounts { get; } = [];
    public ObservableCollection<ArticleRow> Items { get; } = [];

    public string Search
    {
        get => _search;
        set => Set(ref _search, value);
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
        ? "全部待采集 · " + Accounts.Sum(row => row.Account.Count) + " 篇"
        : "全部公众号 · " + Accounts.Sum(row => row.Account.Count) + " 篇";
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
        ? "共 " + _total + " 篇待采集" + (_selectedAccount is null ? "" : " · " + _selectedAccount.Name)
        : "共 " + _total + " 篇" + (_selectedAccount is null ? "" : " · " + _selectedAccount.Name);
    public bool IsEmpty => Items.Count == 0;
    public string EmptyText => _search.Length > 0
        ? "没有匹配的文章。"
        : _isPendingView
            ? "还没有待采集文章。去「采集」页嗅探，或点「清理旧待采集」删除旧记录。"
            : _selectedAccount is not null
                ? "这个公众号还没有文章。"
                : "资料库还是空的，去「采集」页开始吧。";

    public bool AllSelected
    {
        get => _allSelected;
        set
        {
            if (!Set(ref _allSelected, value)) return;
            foreach (var item in Items) item.Selected = value;
        }
    }

    public ICommand RefreshCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand PrevCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand ToggleSortCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand ReadCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand SavePendingCommand { get; }
    public ICommand SaveSelectedPendingCommand { get; }
    public ICommand CleanupLegacyCommand { get; }
    public ICommand RetryFailedCommand { get; }
    public ICommand ReexportSelectedCommand { get; }
    public ICommand DeleteCommand { get; }

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
        if (result.Items.Count == 0 && result.Total > 0 && _page > 1)
        {
            _page = Math.Max(1, (int)Math.Ceiling(result.Total / (double)PageSize));
            result = _library.Query(new LibraryQuery(_search, _selectedAccount?.Account.Biz, _state, _page, PageSize, _descending));
        }
        Items.Clear();
        foreach (var article in result.Items) Items.Add(new ArticleRow(article));
        _total = result.Total;
        _totalPages = Math.Max(1, (int)Math.Ceiling(result.Total / (double)PageSize));
        _allSelected = false;
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(AllSelected));
    }

    private void LoadPendingArticles()
    {
        var rows = _pending.LoadAll()
            .Where(account => _selectedAccount is null || account.Biz == _selectedAccount.Account.Biz)
            .SelectMany(account => account.Articles.Select(article => new { Account = account, Article = article }))
            .Where(row => string.IsNullOrWhiteSpace(_search) ||
                          row.Article.Title.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
                          (row.Account.Account ?? "").Contains(_search, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var ordered = _descending
            ? rows.OrderByDescending(row => row.Article.PublishedAt ?? DateTimeOffset.MinValue)
            : rows.OrderBy(row => row.Article.PublishedAt ?? DateTimeOffset.MinValue);
        var list = ordered.ToArray();
        _total = list.Length;
        _totalPages = Math.Max(1, (int)Math.Ceiling(list.Length / (double)PageSize));
        if (_page > _totalPages) _page = _totalPages;
        Items.Clear();
        foreach (var row in list.Skip((_page - 1) * PageSize).Take(PageSize))
        {
            var accountName = string.IsNullOrWhiteSpace(row.Article.Account)
                ? (string.IsNullOrWhiteSpace(row.Account.Account) ? _library.GetAccountName(row.Account.Biz) ?? "" : row.Account.Account)
                : row.Article.Account;
            if (string.IsNullOrWhiteSpace(accountName)) accountName = row.Account.Biz;
            var article = new Article
            {
                Id = row.Article.Id,
                Url = row.Article.Url,
                Biz = row.Article.Biz,
                Account = accountName,
                Title = row.Article.Title,
                Author = row.Article.Author,
                PublishedAt = row.Article.PublishedAt,
                State = ContentState.NotFetched,
                LastError = row.Article.LastError
            };
            Items.Add(new ArticleRow(article, row.Article with { Account = accountName }));
        }
        _allSelected = false;
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(AllSelected));
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
        LoadAccounts();
        LoadArticles();
    }

    private void ReexportSelected()
    {
        if (_isPendingView)
        {
            _status("待采集文章还没有正文，保存后再导出。");
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
        if (_actions.IsBusy) { _status("正在采集、保存或导出，请等待当前任务结束。"); return; }
        var selected = Items.Where(item => item.Selected).ToArray();
        if (selected.Length == 0)
        {
            _status(_isPendingView ? "请先勾选要删除的待采集文章。" : "请先勾选要删除的文章。");
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
}
