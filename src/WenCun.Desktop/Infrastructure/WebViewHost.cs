using System.IO;
using Microsoft.Web.WebView2.Core;

namespace WenCun.Desktop.Infrastructure;

public static class WebViewHost
{
    private static (string Root, CoreWebView2Environment Environment)? _cached;

    public static async Task<CoreWebView2Environment> GetAsync(string appRoot)
    {
        var root = Path.GetFullPath(appRoot);
        if (_cached is { } cached && string.Equals(cached.Root, root, StringComparison.OrdinalIgnoreCase))
            return cached.Environment;
        var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(root, "webview"));
        _cached = (root, environment);
        return environment;
    }
}
