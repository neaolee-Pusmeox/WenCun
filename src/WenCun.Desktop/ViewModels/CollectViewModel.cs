using System.Windows.Input;
using System.IO;
using WenCun.Capture;
using WenCun.Core.Collect;
using WenCun.Core.Domain;
using WenCun.Core.Export;
using WenCun.Core.Net;
using WenCun.Core.Pending;
using WenCun.Core.Storage;
using WenCun.Desktop.Infrastructure;

namespace WenCun.Desktop.ViewModels;

public sealed class CollectViewModel : ObservableObject
{
    private Library _library;
    private readonly string _appRoot;
    private readonly IPdfRenderer _pdfRenderer;
    private readonly PendingStore _pending;
    private readonly Action _discovered;
    private readonly Action _refreshPending;
    private readonly Func<bool> _canStart;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource? _stopped;
    private Session? _session;
    private string? _expectedBiz;
    private string _expectedAccount = "";
    private bool _consent;
    private bool _busy;
    private string _state = "从公众号获取文章目录";
    private string _detail = "在微信中打开目标公众号的一篇文章。";
    private bool _showCertificate;
    private int _pages;
    private int _found;
    private int _pendingCount;

    public CollectViewModel(Library library, string appRoot, IPdfRenderer pdfRenderer,
        PendingStore pending, Action discovered, Action refreshPending, Func<bool>? canStart = null)
    {
        _library = library;
        _appRoot = appRoot;
        _pdfRenderer = pdfRenderer;
        _pending = pending;
        _discovered = discovered;
        _refreshPending = refreshPending;
        _canStart = canStart ?? (() => true);
        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsBusy);
        StopCommand = new RelayCommand(() => _cts?.Cancel());
        ChangeLibraryCommand = new RelayCommand(() => ChangeLibraryRequested?.Invoke());
        RemoveCertificateCommand = new RelayCommand(() =>
        {
            if (IsBusy) { Detail = "请先停止获取目录，再移除证书。"; return; }
            try
            {
                LocalCaptureCertificate.RemovePersistent();
                State = "本地证书已移除";
                Detail = "下次获取目录时会重新安装。";
            }
            catch (Exception ex)
            {
                State = "移除证书失败";
                Detail = ex.Message;
            }
        });
        ToggleCertificateCommand = new RelayCommand(() => ShowCertificate = !ShowCertificate);
    }

    public event Action? ChangeLibraryRequested;

    public bool Consent { get => _consent; set => Set(ref _consent, value); }
    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value)) OnPropertyChanged(nameof(CanEditSettings));
        }
    }
    public bool CanEditSettings => !IsBusy;
    public bool ShowCertificate { get => _showCertificate; private set => Set(ref _showCertificate, value); }
    public string State { get => _state; private set => Set(ref _state, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public int PageCount => _pages;
    public int FoundCount => _found;
    public int PendingCount => _pendingCount;
    public Session? LastSession => _session;
    public Task StopAsync()
    {
        _cts?.Cancel();
        return _stopped?.Task ?? Task.CompletedTask;
    }
    public string LibraryPath => _library.RootDirectory;

    public bool SaveHtml { get => HasFormat("Html"); set => SetFormat("Html", value); }
    public bool SaveMarkdown { get => HasFormat("Markdown"); set => SetFormat("Markdown", value); }
    public bool SaveText { get => HasFormat("Text"); set => SetFormat("Text", value); }
    public bool SaveWord { get => HasFormat("Word"); set => SetFormat("Word", value); }
    public bool SavePdf { get => HasFormat("Pdf"); set => SetFormat("Pdf", value); }

    public bool ImagesDownload { get => App.Config.Images == "Download"; set { if (value) SetImages("Download"); } }
    public bool ImagesLink { get => App.Config.Images == "Link"; set { if (value) SetImages("Link"); } }
    public bool ImagesNone { get => App.Config.Images == "None"; set { if (value) SetImages("None"); } }

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ChangeLibraryCommand { get; }
    public ICommand RemoveCertificateCommand { get; }
    public ICommand ToggleCertificateCommand { get; }

    public void SetLibrary(Library library)
    {
        _library = library;
        _session = null;
        OnPropertyChanged(nameof(LastSession));
        OnPropertyChanged(nameof(LibraryPath));
    }

    public void SetTarget(Account account)
    {
        _expectedBiz = account.Biz;
        _expectedAccount = string.IsNullOrWhiteSpace(account.Name) ? account.Biz : account.Name;
        State = "获取“" + _expectedAccount + "”的目录";
        Detail = "点击“获取目录”，然后在微信中打开这个公众号的一篇文章。";
    }

    private bool HasFormat(string name) =>
        (App.Config.Formats ?? []).Contains(name, StringComparer.OrdinalIgnoreCase);

    private void SetFormat(string name, bool enabled)
    {
        var formats = (App.Config.Formats ?? []).ToList();
        if (enabled)
        {
            if (!formats.Contains(name, StringComparer.OrdinalIgnoreCase)) formats.Add(name);
        }
        else formats.RemoveAll(format => string.Equals(format, name, StringComparison.OrdinalIgnoreCase));
        App.Config.Formats = formats.ToArray();
        App.Config.Save(_appRoot);
        OnPropertyChanged(nameof(SaveHtml));
        OnPropertyChanged(nameof(SaveMarkdown));
        OnPropertyChanged(nameof(SaveText));
        OnPropertyChanged(nameof(SaveWord));
        OnPropertyChanged(nameof(SavePdf));
    }

    private void SetImages(string mode)
    {
        App.Config.Images = mode;
        App.Config.Save(_appRoot);
        OnPropertyChanged(nameof(ImagesDownload));
        OnPropertyChanged(nameof(ImagesLink));
        OnPropertyChanged(nameof(ImagesNone));
    }

    private async Task StartAsync()
    {
        if (IsBusy) return;
        if (!_canStart()) { State = "另一项任务正在进行"; Detail = "保存或导出结束后即可获取目录。"; return; }
        if (!Consent)
        {
            State = "请先同意证书和代理设置";
            return;
        }
        IsBusy = true;
        _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pages = _found = _pendingCount = 0;
        NotifyCounts();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        CaptureProxy? proxy = null;
        try
        {
            proxy = new CaptureProxy(_appRoot, _expectedBiz);
            var captured = new TaskCompletionSource<Session>(TaskCreationOptions.RunContinuationsAsynchronously);
            proxy.Captured += session => captured.TrySetResult(session);
            State = "正在准备连接";
            Detail = "首次使用时，请在 Windows 提示中允许安装本地证书。";
            await Task.Run(proxy.Start);
            State = _expectedAccount.Length > 0
                ? $"请在微信中打开“{_expectedAccount}”的一篇文章"
                : "请在微信中打开目标公众号的一篇文章";
            Detail = "从公众号主页进入历史消息，选择一篇近期没打开过的文章。等待时间最长为 3 分钟。";
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            var session = await captured.Task.WaitAsync(linked.Token);
            _session = session;
            OnPropertyChanged(nameof(LastSession));
            State = "正在读取文章目录";
            Detail = "已连接微信，正在恢复网络设置。";
            var warnings = await proxy.StopAsync();
            if (warnings.Count > 0) throw new IOException(string.Join("；", warnings));
            State = "正在读取文章目录";
            Detail = "目录会逐页显示在下方，此时不会保存文章正文。";
            using var client = new WeChatClient();
            var collector = new Collector(_library, client, _pending, App.Config.ToSaveOptions(), _pdfRenderer);
            var progress = new Progress<CollectProgress>(report =>
            {
                _pages = report.Pages;
                _found = report.Found;
                _pendingCount = report.Saved;
                NotifyCounts();
                Detail = report.Detail;
                _refreshPending();
            });
            var name = string.IsNullOrWhiteSpace(session.Account) ? _expectedAccount : session.Account;
            var total = await collector.DiscoverAsync(session, session.Biz, name, progress, ct);
            State = "目录已获取";
            Detail = $"找到 {total} 篇待保存文章，在下方勾选后保存。";
            _discovered();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            State = "没有读取到微信文章";
            Detail = "请从公众号主页进入历史消息，打开一篇近期没读过的文章后重试。";
        }
        catch (OperationCanceledException)
        {
            State = "已停止";
            Detail = "已获取的文章目录已保留。";
        }
        catch (Exception ex)
        {
            State = "获取目录失败";
            Detail = ex.Message;
        }
        finally
        {
            if (proxy is not null)
            {
                try
                {
                    var warnings = await proxy.StopAsync();
                    if (warnings.Count > 0) Detail = string.Join("；", warnings);
                    await proxy.DisposeAsync();
                }
                catch (Exception ex) { Detail = "清理采集代理失败：" + ex.Message; }
            }
            _cts?.Dispose();
            _cts = null;
            IsBusy = false;
            _expectedBiz = null;
            _expectedAccount = "";
            _stopped.TrySetResult();
        }
    }

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(FoundCount));
        OnPropertyChanged(nameof(PendingCount));
    }
}
