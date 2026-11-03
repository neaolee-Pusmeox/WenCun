using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using WenCun.Core.Domain;
using WenCun.Core.Export;
using WenCun.Core.Storage;
using WenCun.Desktop.Infrastructure;

namespace WenCun.Desktop.Views;

public partial class ReaderWindow : Window
{
    private readonly Article _article;
    private readonly ArticleContent _content;
    private readonly Library _library;
    private readonly string _appRoot;
    private string? _folder;
    private bool _closed;

    public ReaderWindow(Article article, ArticleContent content, Library library, string appRoot)
    {
        InitializeComponent();
        _article = article;
        _content = content;
        _library = library;
        _appRoot = appRoot;
        Title = article.Title;
        SourceInitialized += (_, _) =>
        {
            WindowTheme.ApplyDarkTitleBar(this, App.Config.Dark);
            WindowTheme.ApplyTextRendering(this, App.Config.Dark);
        };
        Closed += (_, _) =>
        {
            _closed = true;
            try { Browser.Dispose(); }
            catch (Exception) { /* WebView2 may already be torn down. */ }
            DeletePreview();
        };
        Loaded += async (_, _) => await ShowAsync();
    }

    private async Task ShowAsync()
    {
        try
        {
            if (_closed) return;
            if (string.IsNullOrWhiteSpace(_article.Id) || _article.Id is "." or ".." ||
                _article.Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new WenCunException(FailureKind.Storage, "文章标识无效，无法打开阅读页。");
            _folder = Path.Combine(Path.GetTempPath(), "WenCun", "preview", _article.Id);
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
            var html = MarkupWriter.Html(_article, _content, map);
            File.WriteAllText(Path.Combine(_folder, "article.html"), html, new UTF8Encoding(false));
            var environment = await WebViewHost.GetAsync(_appRoot);
            if (_closed) return;
            await Browser.EnsureCoreWebView2Async(environment);
            if (_closed) return;
            Browser.CoreWebView2.SetVirtualHostNameToFolderMapping("wencun.local", _folder, CoreWebView2HostResourceAccessKind.Allow);
            Browser.CoreWebView2.Navigate("https://wencun.local/article.html");
        }
        catch (Exception ex)
        {
            if (!_closed) MessageBox.Show(this, ex.Message, "文存", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
        }
    }

    private void DeletePreview()
    {
        if (_folder is null) return;
        try { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
