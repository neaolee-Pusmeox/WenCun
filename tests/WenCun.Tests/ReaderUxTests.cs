using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WenCun.Core.Domain;
using WenCun.Core.Storage;
using WenCun.Desktop.Views;
using Xunit.Abstractions;

namespace WenCun.Tests;

[CollectionDefinition("Reader UI", DisableParallelization = true)]
public sealed class ReaderUiCollection;

[Collection("Reader UI")]
public sealed class ReaderUxTests(ITestOutputHelper output)
{
    [Fact]
    public void InitialLoadingIsNamedAndCanBeClosed() => OnUi(async fixture =>
    {
        var window = fixture.Window();
        var status = Element<TextBlock>(window, "StatusTitle");
        Assert.Equal("正在打开文章…", status.Text);
        Assert.Equal(Visibility.Visible, Element<FrameworkElement>(window, "StatusPanel").Visibility);
        Assert.Equal("关闭阅读窗口", AutomationProperties.GetName(Element<Button>(window, "CloseButton")));
        Assert.Equal(Visibility.Hidden, Element<FrameworkElement>(window, "Browser").Visibility);
        await Task.CompletedTask;
    });

    [Fact]
    public void SameArticleWindowsOwnSeparateFilesAndClosingOnePreservesTheOther() => OnUi(async fixture =>
    {
        var first = fixture.Window();
        var second = fixture.Window();
        StubBrowser(first, () => { Complete(first, true); return Task.CompletedTask; });
        StubBrowser(second, () => { Complete(second, true); return Task.CompletedTask; });
        await Task.WhenAll(Open(first), Open(second));
        var firstFolder = Folder(first);
        var secondFolder = Folder(second);
        Assert.NotEqual(firstFolder, secondFolder);
        Assert.True(File.Exists(Path.Combine(firstFolder, "article.html")));
        first.Close();
        Assert.False(Directory.Exists(firstFolder));
        Assert.True(File.Exists(Path.Combine(secondFolder, "article.html")));
        second.Close();
        Assert.False(Directory.Exists(secondFolder));
    });

    [Fact]
    public void LockedAssetFailureStaysOpenAndRetryRecovers() => OnUi(async fixture =>
    {
        var asset = Path.Combine(fixture.Library.AssetsDirectory, "fixture.txt");
        File.WriteAllText(asset, "temporary fixture");
        var window = fixture.Window([new Asset("https://fixture.invalid/image", "image", "assets/fixture.txt")]);
        var attempts = 0;
        StubBrowser(window, () => { attempts++; Complete(window, true); return Task.CompletedTask; });
        using (File.Open(asset, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Open(window);
        Assert.Equal(0, attempts);
        AssertFailed(window);
        Assert.DoesNotContain(fixture.Root, Element<TextBlock>(window, "StatusDetail").Text);
        await Open(window);
        Assert.Equal(1, attempts);
        Assert.Equal(Visibility.Collapsed, Element<FrameworkElement>(window, "StatusPanel").Visibility);
        Assert.Equal(Visibility.Visible, Element<FrameworkElement>(window, "Browser").Visibility);
    });

    [Fact]
    public void InitializationAndNavigationFailuresAreRecoverable() => OnUi(async fixture =>
    {
        var window = fixture.Window();
        StubBrowser(window, () => Task.FromException(new InvalidOperationException("synthetic renderer failure")));
        await Open(window);
        AssertFailed(window);
        StubBrowser(window, () => Task.CompletedTask);
        await Open(window);
        Assert.Equal("正在打开文章…", Element<TextBlock>(window, "StatusTitle").Text);
        Complete(window, false);
        AssertFailed(window);
        await Open(window);
        Complete(window, true);
        Assert.Equal(Visibility.Collapsed, Element<FrameworkElement>(window, "StatusPanel").Visibility);
        Assert.False(Field<bool>(window, "_closed"));
    });

    [Fact]
    public void ClosingDuringInitializationIgnoresLateFailureAndCleansPreview() => OnUi(async fixture =>
    {
        var window = fixture.Window();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        StubBrowser(window, () => { attempts++; started.SetResult(); return completion.Task; });
        var opening = Open(window);
        await started.Task;
        await Open(window);
        Assert.Equal(1, attempts);
        var folder = Folder(window);
        window.Close();
        completion.SetException(new InvalidOperationException("late renderer failure"));
        await opening;
        Complete(window, false);
        await Open(window);
        Assert.Equal(1, attempts);
        Assert.True(Field<bool>(window, "_closed"));
        Assert.False(Directory.Exists(folder));
    });

    [Theory]
    [InlineData("https://wencun.local/article.html", true)]
    [InlineData("https://wencun.local/article.html#section", true)]
    [InlineData("https://wencun.local/other.html", false)]
    [InlineData("https://wencun.local/article.html?outside=1", false)]
    [InlineData("https://wencun.local:444/article.html", false)]
    [InlineData("https://fixture.invalid/", false)]
    [InlineData("file:///C:/Windows/win.ini", false)]
    [InlineData("javascript:alert(1)", false)]
    public void NavigationKeepsTheLocalArticle(string uri, bool allowed)
    {
        var method = typeof(ReaderWindow).GetMethod("IsArticleNavigation", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.Equal(allowed, method.Invoke(null, [uri]));
    }

    [Theory]
    [InlineData("https://fixture.invalid/article", true, true)]
    [InlineData("http://fixture.invalid/article", true, true)]
    [InlineData("https://fixture.invalid/article", false, false)]
    [InlineData("http://fixture.invalid/article", false, false)]
    [InlineData("https://fixture@fixture.invalid/article", true, false)]
    [InlineData("https://fixture:synthetic@fixture.invalid/article", true, false)]
    [InlineData("file:///C:/Windows/win.ini", true, false)]
    [InlineData("javascript:alert(1)", true, false)]
    [InlineData("mailto:fixture@example.invalid", true, false)]
    [InlineData("relative/link", true, false)]
    public void ExternalOpeningRequiresAnExplicitUserAndSafeWebAddress(string address, bool userInitiated, bool opens) => OnUi(async fixture =>
    {
        var window = fixture.Window();
        var opened = new List<Uri>();
        StubExternal(window, opened.Add);
        Invoke(window, "OpenExternalLink", address, userInitiated);
        Assert.Equal(opens ? 1 : 0, opened.Count);
        if (opens) Assert.Equal(address, opened[0].AbsoluteUri);
        Assert.False(Field<bool>(window, "_closed"));
        await Task.CompletedTask;
    });

    [Fact]
    public void ExternalLaunchFailurePreservesReadyArticleAndClosingRejectsLaunches() => OnUi(async fixture =>
    {
        var window = fixture.Window();
        StubBrowser(window, () => { Complete(window, true); return Task.CompletedTask; });
        await Open(window);
        var attempts = 0;
        StubExternal(window, _ => { attempts++; throw new InvalidOperationException("synthetic launch failure"); });
        Invoke(window, "OpenExternalLink", "https://fixture.invalid/article", true);
        Assert.Equal(1, attempts);
        Assert.Equal(Visibility.Visible, Element<FrameworkElement>(window, "Browser").Visibility);
        Assert.Equal(Visibility.Collapsed, Element<FrameworkElement>(window, "StatusPanel").Visibility);
        Assert.Equal("未能打开浏览器，请重试。", Element<TextBlock>(window, "NavigationMessage").Text);
        Assert.False(Field<bool>(window, "_closed"));
        window.Close();
        Invoke(window, "OpenExternalLink", "https://fixture.invalid/article", true);
        Assert.Equal(1, attempts);
    });

    [Fact]
    public void NativeShellAndPreviewUseBothThemesAtMinimumAndThousandPixelWidths() => OnUi(async fixture =>
    {
        var window = fixture.Window();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        StubBrowser(window, () => { started.TrySetResult(); return completion.Task; });
        // NativeUiTests can already have shut down WPF's process-wide Application;
        // trigger loading explicitly instead of depending on its deferred Loaded event.
        var opening = Open(window);
        window.Show();
        await started.Task;
        foreach (var theme in new[] { "Light", "Dark" })
        {
            fixture.Theme(window, theme);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            foreach (var width in new[] { 720, 1000 })
            {
                window.Width = width;
                window.Height = 520;
                Layout(window);
                var css = (string)Invoke(window, "BuildReaderStyles")!;
                var canvas = ((SolidColorBrush)window.FindResource("CanvasBrush")).Color;
                Assert.Contains($"--paper:#{canvas.R:X2}{canvas.G:X2}{canvas.B:X2}", css);
                Assert.Contains(theme == "Dark" ? "color-scheme:dark" : "color-scheme:light", css);
                Assert.Contains("@media screen", css);
                Assert.Equal(theme == "Dark" ? TextRenderingMode.Grayscale : TextRenderingMode.ClearType,
                    TextOptions.GetTextRenderingMode(window));
                Capture(window, fixture.Root, $"reader-loading-{theme}-{width}.png");
            }
        }
        completion.SetException(new IOException("synthetic failure"));
        await opening;
        AssertFailed(window);
        foreach (var theme in new[] { "Light", "Dark" })
        {
            fixture.Theme(window, theme);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.Width = 720;
            Layout(window);
            Assert.True(Element<Button>(window, "RetryButton").ActualWidth >= 60);
            Capture(window, fixture.Root, $"reader-failed-{theme}-720.png");
        }
    });

    private void Capture(Window window, string root, string name)
    {
        var surface = (FrameworkElement)window.Content;
        Assert.True(surface.ActualWidth > 0 && surface.ActualHeight > 0, "Reader shell must be laid out before capture.");
        var bitmap = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(root, name);
        using var stream = File.Create(path);
        encoder.Save(stream);
        output.WriteLine("Screenshot: " + path);
    }

    private static void Layout(Window window)
    {
        var surface = (FrameworkElement)window.Content;
        surface.Measure(new Size(window.Width, window.Height));
        surface.Arrange(new Rect(0, 0, window.Width, window.Height));
        surface.UpdateLayout();
    }

    private static void AssertFailed(ReaderWindow window)
    {
        Assert.False(Field<bool>(window, "_closed"));
        Assert.Equal("文章未能打开", Element<TextBlock>(window, "StatusTitle").Text);
        Assert.Equal(Visibility.Visible, Element<Button>(window, "RetryButton").Visibility);
        Assert.True(Element<Button>(window, "RetryButton").IsEnabled);
        Assert.Equal(Visibility.Hidden, Element<FrameworkElement>(window, "Browser").Visibility);
    }

    private static T Element<T>(ReaderWindow window, string name) => Assert.IsAssignableFrom<T>(window.FindName(name));
    private static T Field<T>(ReaderWindow window, string name) => (T)typeof(ReaderWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
    private static string Folder(ReaderWindow window) => Field<string>(window, "_folder");
    private static Task Open(ReaderWindow window) => (Task)Invoke(window, "ShowAsync")!;
    private static void Complete(ReaderWindow window, bool success) => Invoke(window, "CompleteNavigation", success);
    private static object? Invoke(ReaderWindow window, string name, params object[] args)
    {
        var method = typeof(ReaderWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return method.Invoke(window, args);
    }

    private static void StubBrowser(ReaderWindow window, Func<Task> initialize)
    {
        var field = typeof(ReaderWindow).GetField("_initializeBrowser", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field.SetValue(window, initialize);
    }

    private static void StubExternal(ReaderWindow window, Action<Uri> open)
    {
        var field = typeof(ReaderWindow).GetField("_openExternal", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field.SetValue(window, open);
    }

    private static void OnUi(Func<Fixture, Task> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                Fixture? fixture = null;
                try
                {
                    fixture = new Fixture();
                    await action(fixture).WaitAsync(TimeSpan.FromSeconds(20));
                }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    if (fixture is not null)
                        foreach (var window in fixture.Windows) if (!Field<bool>(window, "_closed")) window.Close();
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Reader UI check timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class Fixture
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "WenCunReaderUxTests", Guid.NewGuid().ToString("N"));
        public Library Library { get; }
        public List<ReaderWindow> Windows { get; } = [];
        public Fixture() => Library = new Library(Path.Combine(Root, "library"));
        public ReaderWindow Window(IReadOnlyList<Asset>? assets = null)
        {
            var article = new Article { Id = "same-fixture", Url = "https://fixture.invalid/article", Title = "阅读恢复测试文章" };
            var content = new ArticleContent(article.Id, "<p>临时测试正文</p>", DateTimeOffset.UtcNow, assets ?? [], []);
            var window = new ReaderWindow(article, content, Library, Root) { ShowInTaskbar = false, ShowActivated = false };
            Theme(window, "Light");
            window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/WenCun;component/Themes/Controls.xaml", UriKind.Relative) });
            Windows.Add(window);
            return window;
        }

        public void Theme(ReaderWindow window, string name)
        {
            var themes = window.Resources.MergedDictionaries;
            var theme = new ResourceDictionary { Source = new Uri($"/WenCun;component/Themes/{name}.xaml", UriKind.Relative) };
            if (themes.Count == 0) themes.Add(theme);
            else themes[0] = theme;
        }
    }
}
