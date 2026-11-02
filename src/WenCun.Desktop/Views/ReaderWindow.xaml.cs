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

    public ReaderWindow(Article article, ArticleContent content, Library library)
    {
        InitializeComponent();
        _article = article;
        _content = content;
        _library = library;
        Title = article.Title;
        Loaded += async (_, _) => await ShowAsync();
    }

    private async Task ShowAsync()
    {
        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "WenCun", "preview", _article.Id);
            Directory.CreateDirectory(folder);
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var asset in _content.Assets)
            {
                if (asset.LocalPath is null) continue;
                var source = Path.Combine(_library.RootDirectory, asset.LocalPath);
                if (!File.Exists(source)) continue;
                var name = Path.GetFileName(source);
                File.Copy(source, Path.Combine(folder, name), true);
                map[asset.Source] = name;
            }
            var html = MarkupWriter.Html(_article, _content, map);
            File.WriteAllText(Path.Combine(folder, "article.html"), html, new UTF8Encoding(false));
            var environment = await WebViewHost.GetAsync(App.AppRoot);
            await Browser.EnsureCoreWebView2Async(environment);
            Browser.CoreWebView2.SetVirtualHostNameToFolderMapping("wencun.local", folder, CoreWebView2HostResourceAccessKind.Allow);
            Browser.CoreWebView2.Navigate("https://wencun.local/article.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "文存", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
        }
    }
}
