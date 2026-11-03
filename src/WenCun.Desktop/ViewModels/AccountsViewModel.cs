using System.Collections.ObjectModel;
using System.Windows.Input;
using WenCun.Core.Domain;
using WenCun.Core.Pending;
using WenCun.Core.Storage;
using WenCun.Desktop.Infrastructure;

namespace WenCun.Desktop.ViewModels;

public sealed class AccountsViewModel : ObservableObject
{
    private Library _library;
    private readonly PendingStore _pending;
    private readonly Action<Account> _view;
    private readonly Action<Account> _collect;
    private readonly Func<Account, Task> _retryFailed;
    private readonly Func<Account, Task> _reexportFailed;
    private readonly Func<Account, Task> _delete;
    private readonly Action<Account> _viewPending;

    public AccountsViewModel(Library library, PendingStore pending, Action<Account> view, Action<Account> collect,
        Func<Account, Task> retryFailed, Func<Account, Task> reexportFailed, Func<Account, Task> delete,
        Action<Account> viewPending)
    {
        _library = library;
        _pending = pending;
        _view = view;
        _collect = collect;
        _retryFailed = retryFailed;
        _reexportFailed = reexportFailed;
        _delete = delete;
        _viewPending = viewPending;
        RefreshCommand = new RelayCommand(Load);
        ViewCommand = new RelayCommand<AccountRow>(row => _view(row.Account));
        CollectCommand = new RelayCommand<AccountRow>(row => _collect(row.Account));
        RetryFailedCommand = new RelayCommand<AccountRow>(row => _ = _retryFailed(row.Account));
        ReexportFailedCommand = new RelayCommand<AccountRow>(row => _ = _reexportFailed(row.Account));
        DeleteCommand = new RelayCommand<AccountRow>(row => _ = _delete(row.Account));
        ViewPendingCommand = new RelayCommand<AccountRow>(row => _viewPending(row.Account));
    }

    public ObservableCollection<AccountRow> Items { get; } = [];
    public int AccountCount => Items.Count;
    public int SavedTotal => Items.Sum(row => row.Account.Saved);
    public int FailedTotal => Items.Sum(row => row.Account.Failed);
    public int PendingTotal => Items.Sum(row => row.Pending);
    public bool IsEmpty => Items.Count == 0;
    public ICommand RefreshCommand { get; }
    public ICommand ViewCommand { get; }
    public ICommand CollectCommand { get; }
    public ICommand RetryFailedCommand { get; }
    public ICommand ReexportFailedCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ViewPendingCommand { get; }

    public void Load()
    {
        var pending = _pending.LoadAll().ToDictionary(account => account.Biz, StringComparer.Ordinal);
        Items.Clear();
        foreach (var account in _library.Accounts())
        {
            if (pending.Remove(account.Biz, out var discovered))
                Items.Add(new AccountRow(account with
                {
                    Name = string.IsNullOrWhiteSpace(discovered.Account) ? account.Name : discovered.Account,
                    Complete = discovered.Complete,
                    LastCollected = discovered.LastDiscovered ?? account.LastCollected
                }, discovered.Articles.Count));
            else Items.Add(new AccountRow(account));
        }
        foreach (var discovered in pending.Values)
            Items.Add(new AccountRow(new Account(discovered.Biz, discovered.Account, 0, 0, 0, 0, 0,
                discovered.Articles.Max(article => article.PublishedAt), discovered.LastDiscovered, discovered.Complete), discovered.Articles.Count));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(AccountCount));
        OnPropertyChanged(nameof(SavedTotal));
        OnPropertyChanged(nameof(FailedTotal));
        OnPropertyChanged(nameof(PendingTotal));
    }

    public void SetLibrary(Library library)
    {
        _library = library;
        Load();
    }
}
