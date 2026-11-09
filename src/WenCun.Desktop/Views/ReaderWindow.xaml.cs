using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WenCun.Core.Domain;
using WenCun.Core.Export;
using WenCun.Core.Storage;
using WenCun.Desktop.Infrastructure;

namespace WenCun.Desktop.Views;

public partial class ReaderWindow : Window
{
    private const string ArticleAddress = "https://wencun.local/article.html";
    private readonly Article _article;
    private readonly ArticleContent _content;
    private readonly Library _library;
    private readonly string _appRoot;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "WenCun", "preview", Guid.NewGuid().ToString("N"));
    private Func<Task> _initializeBrowser;
    private Action<Uri> _openExternal = uri =>
    {
        using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    };
    private CoreWebView2? _core;
    private bool _closed;
    private bool _loading;
    private bool _awaitingNavigation;

    public ReaderWindow(Article article, ArticleContent content, Library library, string appRoot)
    {
        InitializeComponent();
        _article = article;
        _content = content;
        _library = library;
        _appRoot = appRoot;
        _initializeBrowser = InitializeBrowserAsync;
        Title = article.Title;
        SourceInitialized += (_, _) => ApplyShellTheme();
        Closed += (_, _) =>
        {
            _closed = true;
            _awaitingNavigation = false;
            DisposeBrowser();
            // A file copy may still be finishing; its finally owns cleanup in that case.
            if (!_loading) DeletePreview();
        };
        Loaded += async (_, _) => await ShowAsync();
    }

    private async Task ShowAsync()
    {
        if (_closed || _loading || _awaitingNavigation) return;
        _loading = true;
        _awaitingNavigation = true;
        try
        {
            Browser.Visibility = Visibility.Hidden;
            StatusPanel.Visibility = Visibility.Visible;
            StatusTitle.Text = "正在打开文章…";
            StatusDetail.Text = "正在准备本地正文和图片。";
            LoadingProgress.Visibility = Visibility.Visible;
            RetryButton.Visibility = Visibility.Collapsed;
            NavigationNotice.Visibility = Visibility.Collapsed;
            AnnounceStatus();
            var styles = BuildReaderStyles();
            await Task.Run(() => PreparePreview(styles));
            if (_closed) return;
            await _initializeBrowser();
        }
        catch (Exception ex)
        {
            ShowFailure(ex switch
            {
                IOException or UnauthorizedAccessException => "无法读取本地文章或图片，请检查文件是否被占用后重试。",
                WebView2RuntimeNotFoundException => "缺少 WebView2 阅读组件。安装 Microsoft Edge WebView2 Runtime 后重试。",
                _ => "阅读组件未能完成加载，请重试。"
            });
        }
        finally
        {
            _loading = false;
            if (_closed) DeletePreview();
        }
    }

    private void PreparePreview(string styles)
    {
        Directory.CreateDirectory(_folder);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var asset in _content.Assets)
        {
            if (asset.LocalPath is null) continue;
            var source = Path.Combine(_library.RootDirectory, asset.LocalPath);
            if (!File.Exists(source)) continue;
            var name = Path.GetFileName(source);
            File.Copy(source, Path.Combine(_folder, name), true);
            map[asset.Source] = name;
        }
        var html = MarkupWriter.Html(_article, _content, map)
            .Replace("</head>", $"<style id=\"wencun-reader-theme\">{styles}</style></head>", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(_folder, "article.html"), html, new UTF8Encoding(false));
    }

    private async Task InitializeBrowserAsync()
    {
        DisposeBrowser();
        BrowserHost.Children.Clear();
        UnregisterName("Browser");
        Browser = new WebView2 { Visibility = Visibility.Hidden };
        AutomationProperties.SetName(Browser, "文章正文");
        RegisterName("Browser", Browser);
        BrowserHost.Children.Add(Browser);
        ApplyShellTheme();
        var environment = await WebViewHost.GetAsync(_appRoot);
        if (_closed) return;
        await Browser.EnsureCoreWebView2Async(environment);
        if (_closed) return;
        _core = Browser.CoreWebView2;
        _core.NavigationStarting += NavigationStarting;
        _core.NavigationCompleted += NavigationCompleted;
        _core.NewWindowRequested += NewWindowRequested;
        _core.ProcessFailed += ProcessFailed;
        _core.SetVirtualHostNameToFolderMapping("wencun.local", _folder, CoreWebView2HostResourceAccessKind.DenyCors);
        _core.Navigate(ArticleAddress);
    }

    private void NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_closed || !ReferenceEquals(sender, _core)) { e.Cancel = true; return; }
        if (IsArticleNavigation(e.Uri)) return;
        e.Cancel = true;
        OpenExternalLink(e.Uri, e.IsUserInitiated && !e.IsRedirected);
    }

    private static bool IsArticleNavigation(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        uri.Host == "wencun.local" && uri.Port == 443 && uri.UserInfo.Length == 0 &&
        uri.AbsolutePath == "/article.html" && uri.Query.Length == 0;

    private void NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (!_closed && ReferenceEquals(sender, _core)) OpenExternalLink(e.Uri, e.IsUserInitiated);
    }

    private void OpenExternalLink(string address, bool userInitiated)
    {
        if (_closed) return;
        if (!userInitiated) return;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 ||
            uri.Host.Length == 0 || uri.Host.Equals("wencun.local", StringComparison.OrdinalIgnoreCase))
        {
            ShowNavigationMessage("此链接无法在浏览器中打开，已保留当前文章。");
            return;
        }
        try
        {
            _openExternal(uri);
            NavigationNotice.Visibility = Visibility.Collapsed;
        }
        catch (Exception)
        {
            ShowNavigationMessage("未能打开浏览器，请重试。");
        }
    }

    private void ShowNavigationMessage(string message)
    {
        NavigationMessage.Text = message;
        NavigationNotice.Visibility = Visibility.Visible;
        UIElementAutomationPeer.FromElement(NavigationMessage)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private async void NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_closed || !ReferenceEquals(sender, _core) || !_awaitingNavigation) return;
        if (!e.IsSuccess) { CompleteNavigation(false); return; }
        if (await RefreshThemeAsync() && ReferenceEquals(sender, _core)) CompleteNavigation(true);
    }

    private void CompleteNavigation(bool success)
    {
        if (_closed || !_awaitingNavigation) return;
        if (!success) { ShowFailure("本地文章未能显示，请重试。"); return; }
        _awaitingNavigation = false;
        StatusPanel.Visibility = Visibility.Collapsed;
        LoadingProgress.Visibility = Visibility.Collapsed;
        Browser.Visibility = Visibility.Visible;
        if (IsActive) Browser.Focus();
    }

    private void ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (!_closed && ReferenceEquals(sender, _core)) ShowFailure("阅读组件已停止，请重试以重新打开文章。");
    }

    private void ShowFailure(string message)
    {
        if (_closed) return;
        _awaitingNavigation = false;
        Browser.Visibility = Visibility.Hidden;
        StatusPanel.Visibility = Visibility.Visible;
        LoadingProgress.Visibility = Visibility.Collapsed;
        StatusTitle.Text = "文章未能打开";
        StatusDetail.Text = message;
        RetryButton.Visibility = Visibility.Visible;
        AnnounceStatus();
        if (IsActive) RetryButton.Focus();
    }

    private void AnnounceStatus() =>
        UIElementAutomationPeer.FromElement(StatusTitle)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);

    private async void Retry_Click(object sender, RoutedEventArgs e) => await ShowAsync();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == BackgroundProperty && _article is not null && !_closed)
            _ = RefreshThemeAsync();
    }

    private bool IsDark => Background is SolidColorBrush brush &&
        brush.Color.R * .299 + brush.Color.G * .587 + brush.Color.B * .114 < 128;

    private void ApplyShellTheme()
    {
        WindowTheme.ApplyDarkTitleBar(this, IsDark);
        WindowTheme.ApplyTextRendering(this, IsDark);
        var canvas = (TryFindResource("CanvasBrush") as SolidColorBrush)?.Color ?? SystemColors.WindowColor;
        Browser.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, canvas.R, canvas.G, canvas.B);
    }

    private async Task<bool> RefreshThemeAsync()
    {
        CoreWebView2? core = null;
        try
        {
            // Resource dictionaries update one property at a time; read the complete palette afterward.
            await Dispatcher.Yield(DispatcherPriority.Loaded);
            if (_closed) return false;
            ApplyShellTheme();
            core = _core;
            if (core is not null)
            {
                var styles = JsonSerializer.Serialize(BuildReaderStyles());
                await core.ExecuteScriptAsync($"document.getElementById('wencun-reader-theme')?.replaceChildren(document.createTextNode({styles}));");
            }
            return !_closed && ReferenceEquals(core, _core);
        }
        catch (Exception)
        {
            if (ReferenceEquals(core, _core)) ShowFailure("阅读主题未能加载，请重试。");
            return false;
        }
    }

    private string BuildReaderStyles()
    {
        string Color(string name)
        {
            var color = (TryFindResource(name + "Brush") as SolidColorBrush)?.Color ?? SystemColors.WindowTextColor;
            return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }
        // Preview-only overrides keep exported HTML and its print contract unchanged.
        return $$"""
            @media screen {
              :root{color-scheme:{{(IsDark ? "dark" : "light")}};--paper:{{Color("Canvas")}};--sheet:{{Color("Surface")}};--ink:{{Color("Text")}};--heading:{{Color("Heading")}};--muted:{{Color("Muted")}};--faint:{{Color("Faint")}};--rule:{{Color("Rule")}};--accent:{{Color("Accent")}};--seal:{{Color("Seal")}};--note:{{Color("WarnSoft")}};--subtle:{{Color("Subtle")}}}
              html{scrollbar-color:{{Color("Border")}} var(--paper)}
              ::-webkit-scrollbar-thumb{background:{{Color("Border")}}}::-webkit-scrollbar-thumb:hover{background:var(--muted)}
              a{text-decoration-color:var(--accent)}a:focus-visible{outline:2px solid var(--accent);outline-offset:3px}
              pre,code,th{background:var(--subtle)}pre code{background:none}
              blockquote{color:var(--muted)}aside{border-color:var(--rule);border-left-color:var(--seal);color:{{Color("Warn")}}}aside strong{color:inherit}
              ::selection{background:{{Color("AccentSoft")}};color:var(--ink)}
              .brand svg [fill="#173b32"],footer svg [fill="#173b32"]{fill:{{Color("LogoGround")}}}
              .brand svg [fill="#f5efe2"],footer svg [fill="#f5efe2"]{fill:{{Color("LogoStroke")}};stroke:var(--rule)}
              .brand svg [stroke="#315441"],footer svg [stroke="#315441"]{stroke:{{Color("LogoGround")}}}
              {{(IsDark ? "main [style]{color:var(--ink)!important;background-color:transparent!important;border-color:var(--rule)!important}main a,main a [style]{color:var(--accent)!important}" : "")}}
            }
            """;
    }

    private void DisposeBrowser()
    {
        try
        {
            if (_core is not null)
            {
                _core.NavigationStarting -= NavigationStarting;
                _core.NavigationCompleted -= NavigationCompleted;
                _core.NewWindowRequested -= NewWindowRequested;
                _core.ProcessFailed -= ProcessFailed;
            }
            Browser.Dispose();
        }
        catch (Exception) { /* WebView2 may already be torn down. */ }
        finally { _core = null; }
    }

    private void DeletePreview()
    {
        try
        {
            if (!Directory.Exists(_folder)) return;
            // The preview is flat: never recurse into an unexpected directory or junction.
            for (var parent = _folder; parent is not null; parent = Path.GetDirectoryName(parent))
                if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) return;
            foreach (var file in Directory.EnumerateFiles(_folder)) File.Delete(file);
            Directory.Delete(_folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
