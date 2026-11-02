using System.IO;
using Microsoft.Web.WebView2.Core;

namespace WenCun.Desktop.Infrastructure;

public static class WebViewHost
{
    private static CoreWebView2Environment? _environment;

    public static async Task<CoreWebView2Environment> GetAsync(string appRoot)
    {
        _environment ??= await CoreWebView2Environment.CreateAsync(null, Path.Combine(appRoot, "webview"));
        return _environment;
    }
}
