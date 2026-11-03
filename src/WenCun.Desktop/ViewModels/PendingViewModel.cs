using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using WenCun.Core.Domain;
using WenCun.Core.Pending;
using WenCun.Desktop.Infrastructure;

namespace WenCun.Desktop.ViewModels;

public sealed class PendingArticleRow(PendingArticle article) : ObservableObject
{
    private bool _selected;

    public PendingArticle Article { get; } = article;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public string Title => string.IsNullOrWhiteSpace(Article.Title) ? "（标题未知）" : Article.Title;
    public string Meta => (string.IsNullOrWhiteSpace(Article.Account) ? "未命名公众号" : Article.Account) +
                          " · " + (Article.PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd") ?? "日期未知");
    public string Url => Article.Url;
    public string Error => Article.LastError ?? "";
    public bool HasError => !string.IsNullOrWhiteSpace(Article.LastError);
}

public sealed class PendingAccountRow(PendingAccount account)
{
    public PendingAccount Account { get; } = account;
    public string Name => string.IsNullOrWhiteSpace(Account.Account) ? Account.Biz : Account.Account;
    public string Counts => Account.Articles.Count + " 篇待采集";
    public string Meta => (Account.LastDiscovered?.ToLocalTime().ToString("MM-dd HH:mm") ?? "尚未探明") +
                          (Account.Complete ? " · 目录已完整" : " · 目录未完成");
}

public sealed class PendingViewModel : ObservableObject
{
    private readonly PendingStore _store;
    private readonly Func<string, string?> _accountName;
    private readonly Action<IReadOnlyList<PendingArticle>> _save;
    private readonly Action<IReadOnlyList<PendingArticle>> _delete;
    private readonly Action<string> _clear;
    private readonly Action<string> _deleteAccount;
    private readonly Action<string> _status;
    private PendingAccountRow? _selectedAccount;
    private bool _allSelected;
    private bool _loading;

    public PendingViewModel(PendingStore store, Func<string, string?> accountName,
        Action<IReadOnlyList<PendingArticle>> save,
        Action<IReadOnlyList<PendingArticle>> delete, Action<string> clear, Action<string> deleteAccount,
        Action<string> status)
    {
        _store = store;
        _accountName = accountName;
        _save = save;
        _delete = delete;
        _clear = clear;
        _deleteAccount = deleteAccount;
        _status = status;
        RefreshCommand = new RelayCommand(Load);
        SelectAllCommand = new RelayCommand(() => SelectedAccount = null);
        SaveCommand = new RelayCommand(SaveSelected);
        DeleteCommand = new RelayCommand(DeleteSelected);
        ClearCommand = new RelayCommand(() =>
        {
            if (_selectedAccount is null) { _status("请先选择公众号。"); return; }
            _clear(_selectedAccount.Account.Biz);
        });
        DeleteAccountCommand = new RelayCommand(() =>
        {
            if (_selectedAccount is null) { _status("请先选择公众号。"); return; }
            _deleteAccount(_selectedAccount.Account.Biz);
        });
        OpenFolderCommand = new RelayCommand(() =>
        {
            try { Process.Start(new ProcessStartInfo(_store.Directory) { UseShellExecute = true }); }
            catch (Exception ex) { _status("打开待采集文件夹失败：" + ex.Message); }
        });
    }

    public ObservableCollection<PendingAccountRow> Accounts { get; } = [];
    public ObservableCollection<PendingArticleRow> Items { get; } = [];

    public PendingAccountRow? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (!Set(ref _selectedAccount, value)) return;
            OnPropertyChanged(nameof(IsAllAccounts));
            OnPropertyChanged(nameof(Summary));
            if (!_loading) LoadItems();
        }
    }

    public bool IsAllAccounts => _selectedAccount is null;
    public bool AllSelected
    {
        get => _allSelected;
        set
        {
            if (!Set(ref _allSelected, value)) return;
            foreach (var item in Items) item.Selected = value;
        }
    }

    public string Summary => _selectedAccount is null
        ? $"共 {Accounts.Sum(row => row.Account.Articles.Count)} 篇待采集"
        : $"{_selectedAccount.Name} · {_selectedAccount.Account.Articles.Count} 篇待采集";
    public bool IsEmpty => Items.Count == 0;
    public string EmptyText => "还没有待采集文章。开始嗅探后，目录会出现在这里。";
    public string Folder => _store.Directory;

    public ICommand RefreshCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand DeleteAccountCommand { get; }
    public ICommand OpenFolderCommand { get; }

    public void Load()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            var biz = _selectedAccount?.Account.Biz;
            Accounts.Clear();
            foreach (var account in _store.LoadAll())
            {
                var name = string.IsNullOrWhiteSpace(account.Account) ? _accountName(account.Biz) ?? account.Biz : account.Account;
                var resolved = account with
                {
                    Account = name,
                    Articles = account.Articles.Select(article => article with
                    {
                        Account = string.IsNullOrWhiteSpace(article.Account) ? name : article.Account
                    }).ToArray()
                };
                Accounts.Add(new PendingAccountRow(resolved));
            }
            _selectedAccount = biz is null ? null : Accounts.FirstOrDefault(row => row.Account.Biz == biz);
            OnPropertyChanged(nameof(SelectedAccount));
            OnPropertyChanged(nameof(IsAllAccounts));
            OnPropertyChanged(nameof(Summary));
            LoadItems();
        }
        finally { _loading = false; }
    }

    public void SelectAccount(string biz)
    {
        SelectedAccount = Accounts.FirstOrDefault(row => row.Account.Biz == biz);
    }

    private void LoadItems()
    {
        var selected = Items.Where(item => item.Selected).Select(item => (item.Article.Biz, item.Article.Id)).ToHashSet();
        Items.Clear();
        var accounts = _selectedAccount is null ? Accounts : [_selectedAccount];
        foreach (var account in accounts)
            foreach (var article in account.Account.Articles)
                Items.Add(new PendingArticleRow(article) { Selected = selected.Contains((article.Biz, article.Id)) });
        _allSelected = Items.Count > 0 && Items.All(item => item.Selected);
        OnPropertyChanged(nameof(AllSelected));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(Summary));
    }

    private void SaveSelected()
    {
        var selected = Items.Where(item => item.Selected).Select(item => item.Article).ToArray();
        if (selected.Length == 0) { _status("请先勾选要保存的文章。"); return; }
        _save(selected);
    }

    private void DeleteSelected()
    {
        var selected = Items.Where(item => item.Selected).Select(item => item.Article).ToArray();
        if (selected.Length == 0) { _status("请先勾选要删除的待采集文章。"); return; }
        _delete(selected);
    }
}
