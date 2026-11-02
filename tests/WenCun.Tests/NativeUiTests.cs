using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WenCun.Core.Domain;
using WenCun.Core.Storage;
using WenCun.Desktop;
using WenCun.Desktop.Infrastructure;
using WenCun.Desktop.ViewModels;
using WenCun.Desktop.Views;
using Xunit.Abstractions;

namespace WenCun.Tests;

public sealed class NativeUiTests(ITestOutputHelper output)
{
    [Fact]
    public void PendingAccountNameIsRenderedInActualWindow()
    {
        Exception? failure = null;
        string? screenshot = null;
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            Application? app = null;
            try
            {
                var root = Path.Combine(Path.GetTempPath(), "WenCunUiTests", Guid.NewGuid().ToString("N"));
                app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                foreach (var theme in new[] { "Light", "Controls" })
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/WenCun;component/Themes/{theme}.xaml", UriKind.Relative) });
                app.Resources.Add("BoolToVisibility", new BoolToVisibilityConverter());
                app.Resources.Add("InverseBoolToVisibility", new InverseBoolToVisibilityConverter());
                app.Resources.Add("Section", new SectionConverter());
                var model = new MainViewModel(new Library(Path.Combine(root, "library")), root, null);
                model.Pending.Merge("fixture-biz", "嗅探名称验证号", [new PendingArticle("fixture-id", "fixture-biz", "嗅探名称验证号",
                    "名称回归测试文章", "", "https://mp.weixin.qq.com/s/fixture", null, DateTimeOffset.UtcNow)], 10, false);
                model.Initialize();
                model.Section = "Collect";
                window = new MainWindow(model) { ShowInTaskbar = false, ShowActivated = false };
                window.Show();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                var labels = Descendants(window).OfType<TextBlock>().Where(label => label.IsVisible).Select(label => label.Text).ToArray();
                Assert.Contains("嗅探名称验证号", labels);
                Assert.Contains(labels, text => text.StartsWith("嗅探名称验证号 ·", StringComparison.Ordinal));
                Assert.Contains("名称回归测试文章", labels);
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                screenshot = Path.Combine(root, "pending-name.png");
                using var image = File.Create(screenshot);
                encoder.Save(image);
            }
            catch (Exception ex) { failure = ex; }
            finally { window?.Close(); app?.Shutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "WPF smoke check timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.NotNull(screenshot);
        output.WriteLine("Screenshot: " + screenshot);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

}
