using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WenCun.Core.Domain;
using WenCun.Core.Export;

namespace WenCun.Desktop.Infrastructure;

public sealed class WebViewPdfRenderer(string appRoot) : IPdfRenderer
{
    private readonly string _appRoot = appRoot;

    public async Task RenderPdfAsync(string htmlPath, string pdfPath, CancellationToken ct)
    {
        await Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            var environment = await WebViewHost.GetAsync(_appRoot);
            var window = new Window
            {
                Width = 1000,
                Height = 800,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                Left = -10000,
                Top = -10000,
                ShowActivated = false
            };
            var browser = new WebView2();
            window.Content = browser;
            window.Show();
            try
            {
                await browser.EnsureCoreWebView2Async(environment);
                var core = browser.CoreWebView2;
                var folder = Path.GetDirectoryName(Path.GetFullPath(htmlPath))!;
                core.SetVirtualHostNameToFolderMapping("wencun.local", folder, CoreWebView2HostResourceAccessKind.Allow);
                var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                core.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
                core.Navigate("https://wencun.local/" + Uri.EscapeDataString(Path.GetFileName(htmlPath)));
                if (!await loaded.Task.WaitAsync(TimeSpan.FromSeconds(30), ct))
                    throw new WenCunException(FailureKind.Renderer, "离线页面未能加载。");
                await core.ExecuteScriptAsync("""
                    (async () => {
                      const images = Array.from(document.images);
                      await Promise.all(images.map(i => i.complete ? Promise.resolve() : new Promise(r => { i.onload = r; i.onerror = r; })));
                      await new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)));
                    })();
                    """);
                var settings = core.Environment.CreatePrintSettings();
                settings.ShouldPrintBackgrounds = true;
                settings.MarginTop = 0.4;
                settings.MarginBottom = 0.4;
                settings.MarginLeft = 0.4;
                settings.MarginRight = 0.4;
                if (!await core.PrintToPdfAsync(pdfPath, settings))
                    throw new WenCunException(FailureKind.Renderer, "PDF 生成失败。");
            }
            catch (WebView2RuntimeNotFoundException)
            {
                throw new WenCunException(FailureKind.Renderer, "未安装 Microsoft Edge WebView2 Runtime，无法导出 PDF。");
            }
            finally
            {
                window.Close();
            }
        }).Task.Unwrap();
    }
}
