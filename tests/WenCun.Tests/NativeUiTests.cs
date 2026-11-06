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
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/WenCun;component/Assets/wencun-mark.xaml", UriKind.Relative) });
                app.Resources.Add("BoolToVisibility", new BoolToVisibilityConverter());
                app.Resources.Add("InverseBoolToVisibility", new InverseBoolToVisibilityConverter());
                app.Resources.Add("Section", new SectionConverter());
                var model = new MainViewModel(new Library(Path.Combine(root, "library")), root, null);
                model.Pending.Merge("fixture-biz", "嗅探名称验证号", [new PendingArticle("fixture-id", "fixture-biz", "嗅探名称验证号",
                    "名称回归测试文章", "", "https://mp.weixin.qq.com/s/fixture", null, DateTimeOffset.UtcNow)], 10, false);
                model.Library.UpsertArticles([new Article
                {
                    Id = "failed-id",
                    Url = "https://mp.weixin.qq.com/s/failed",
                    Biz = "fixture-biz",
                    Account = "嗅探名称验证号",
                    Title = "保存失败后仍可阅读的文章"
                }]);
                model.Library.SaveContent(new ArticleContent("failed-id", "<p>正文</p>", DateTimeOffset.UtcNow, [], []), ContentState.Ready);
                model.Library.SetFailure("failed-id", "上次保存失败");
                model.Library.UpsertArticles([new Article
                {
                    Id = "ready-id",
                    Url = "https://mp.weixin.qq.com/s/ready",
                    Biz = "fixture-biz",
                    Account = "嗅探名称验证号",
                    Title = "保存成功的文章"
                }]);
                model.Library.SaveContent(new ArticleContent("ready-id", "<p>正文</p>", DateTimeOffset.UtcNow, [], []), ContentState.Ready);
                model.Initialize();
                window = new MainWindow(model) { ShowInTaskbar = false, ShowActivated = false };
                window.Show();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                var labels = Descendants(window).OfType<TextBlock>().Where(label => label.IsVisible).Select(label => label.Text).ToArray();
                Assert.Contains("嗅探名称验证号", labels);
                Assert.Contains(labels, text => text.StartsWith("嗅探名称验证号，", StringComparison.Ordinal));
                Assert.Contains("名称回归测试文章", labels);
                Assert.Contains("获取公众号目录", labels);
                Assert.Contains("保存所选", labels);
                Assert.DoesNotContain(labels, text => text.Contains("嗅探会话", StringComparison.Ordinal));
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                screenshot = Path.Combine(root, "pending-name.png");
                using var image = File.Create(screenshot);
                encoder.Save(image);
                window.Width = 1000;
                window.Height = 700;
                model.Section = "Library";
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                var actions = Descendants(window).OfType<Button>()
                    .Where(button => button.Content is "阅读" or "重试" or "文件夹" && button.IsVisible).ToArray();
                Assert.Equal(5, actions.Length);
                Assert.All(actions, button => Assert.True(button.ActualWidth >= 40));
                var failedTitle = Descendants(window).OfType<TextBlock>().Single(label => label.Text == "保存失败后仍可阅读的文章");
                var failedRow = Ancestors(failedTitle).OfType<Grid>().First(grid => grid.ColumnDefinitions.Count == 4);
                var readyTitle = Descendants(window).OfType<TextBlock>().Single(label => label.Text == "保存成功的文章");
                var readyRow = Ancestors(readyTitle).OfType<Grid>().First(grid => grid.ColumnDefinitions.Count == 4);
                var read = actions.Single(button => button.Content is "阅读" && Ancestors(button).Contains(failedRow));
                var retry = actions.Single(button => button.Content is "重试");
                var readPosition = read.TransformToAncestor(window).Transform(new Point(0, 0));
                var retryPosition = retry.TransformToAncestor(window).Transform(new Point(0, 0));
                var container = (FrameworkElement)read.Parent;
                Assert.True(readPosition.X + read.ActualWidth <= retryPosition.X,
                    $"Window {window.ActualWidth}, actions {container.ActualWidth}, read {readPosition.X}/{read.ActualWidth}, retry {retryPosition.X}/{retry.ActualWidth}");
                var failedState = Descendants(failedRow).OfType<TextBlock>().Single(label => label.IsVisible && label.Text == "采集失败");
                var readyState = Descendants(readyRow).OfType<TextBlock>().Single(label => label.IsVisible && label.Text == "已保存");
                var failedStatePill = Ancestors(failedState).OfType<Border>().First();
                var readyStatePill = Ancestors(readyState).OfType<Border>().First();
                var failedStateCell = (FrameworkElement)failedStatePill.Parent;
                var readyStateCell = (FrameworkElement)readyStatePill.Parent;
                var stateHeader = Descendants(window).OfType<TextBlock>().Single(label => label.IsVisible && label.Text == "状态");
                Assert.Equal(Center(failedStatePill, window).X, Center(readyStatePill, window).X, 0.5);
                Assert.Equal(Center(failedStateCell, window).X, Center(failedStatePill, window).X, 0.5);
                Assert.Equal(Center(readyStateCell, window).X, Center(readyStatePill, window).X, 0.5);
                Assert.Equal(Center(failedStateCell, window).X, Center(stateHeader, window).X, 0.5);
                var readyRead = actions.Single(button => button.Content is "阅读" && Ancestors(button).Contains(readyRow));
                Assert.Equal(Center(failedStatePill, window).Y, Center(read, window).Y, 0.5);
                Assert.Equal(Center(readyStatePill, window).Y, Center(readyRead, window).Y, 0.5);
                foreach (var section in new[] { "Collect", "Library", "Accounts" })
                {
                    model.Section = section;
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();
                    var pageBitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    pageBitmap.Render(window);
                    var pageEncoder = new PngBitmapEncoder();
                    pageEncoder.Frames.Add(BitmapFrame.Create(pageBitmap));
                    var pageScreenshot = Path.Combine(root, section + ".png");
                    using var pageImage = File.Create(pageScreenshot);
                    pageEncoder.Save(pageImage);
                    output.WriteLine("Screenshot: " + pageScreenshot);
                }
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

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject child)
    {
        for (var parent = VisualTreeHelper.GetParent(child); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            yield return parent;
    }

    private static Point Center(FrameworkElement element, Visual relativeTo)
    {
        var position = element.TransformToAncestor(relativeTo).Transform(new Point(0, 0));
        return new Point(position.X + element.ActualWidth / 2, position.Y + element.ActualHeight / 2);
    }

}
