using System.Runtime.ExceptionServices;
using System.Windows;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WenCun.Core.Domain;
using WenCun.Core.Storage;
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
                var time = new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);
                model.Pending.Merge("fixture-biz", "嗅探名称验证号", Enumerable.Range(0, 36).Select(i =>
                    new PendingArticle("pending-" + i, "fixture-biz", "嗅探名称验证号", "名称回归测试文章" + i, "",
                        "https://mp.weixin.qq.com/s/fixture-" + i, time, time)).ToArray(), 36, false);
                model.Pending.Merge("filter-biz", "公众号筛选验证号", Enumerable.Range(0, 5).Select(i =>
                    new PendingArticle("filter-" + i, "filter-biz", "公众号筛选验证号", "筛选测试文章" + i, "",
                        "https://mp.weixin.qq.com/s/filter-" + i, time, time)).ToArray(), 5, true);
                foreach (var i in Enumerable.Range(0, 16))
                {
                    var id = "article-" + i;
                    model.Library.UpsertArticles([new Article
                    {
                        Id = id, Url = "https://mp.weixin.qq.com/s/fixture-" + id, Biz = "fixture-biz",
                        Account = "嗅探名称验证号", PublishedAt = time.AddDays(-i),
                        Title = i == 0 ? "保存失败后仍可阅读的文章" : i == 1 ? "保存成功的文章" : "窄窗口下应能辨认的长文章标题" + i
                    }]);
                    model.Library.SaveContent(new ArticleContent(id, "<p>临时测试正文</p>", time, [], []), ContentState.Ready);
                }
                model.Library.SetFailure("article-0", "测试保存失败");
                model.Library.SetExportResult("article-2", ExportState.Failed, "测试导出失败");
                model.Initialize();
                window = new MainWindow(model) { ShowInTaskbar = false };
                // Use the requested logical sizes even when the desktop work area is smaller.
                window.MaxWidth = double.PositiveInfinity;
                window.MaxHeight = double.PositiveInfinity;
                window.Show();
                var failures = new List<string>();
                var collectSizes = new Dictionary<(string Theme, double Width, double Height), (double Window, double Content, double Panel, double List)>();
                void Check(string scenario, Action assertion)
                {
                    try { assertion(); }
                    catch (Exception ex) { failures.Add(scenario + ": " + ex); }
                }
                foreach (var theme in new[] { "Light", "Dark" })
                foreach (var size in new[]
                {
                    new Size(1000, 640), new Size(1000, 1000),
                    new Size(1179, 640), new Size(1180, 640), new Size(1181, 640),
                    new Size(1280, 640), new Size(1280, 840), new Size(1600, 900)
                })
                {
                    app.Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri($"/WenCun;component/Themes/{theme}.xaml", UriKind.Relative) };
                    WindowTheme.ApplyTextRendering(window, theme == "Dark");
                    window.Width = size.Width;
                    window.Height = size.Height;
                    var context = $"{theme}-{size.Width}x{size.Height}";
                    var sections = size == new Size(1000, 640) || size == new Size(1280, 840)
                        ? new[] { "Library", "Collect", "Accounts" } : new[] { "Collect" };
                    foreach (var section in sections)
                    {
                        model.Section = section;
                        window.Activate();
                        Settle(window);
                        Capture(window, root, context + "-" + section);
                        Check(context + "-" + section + " automation", () => AssertAutomation(window));
                        Check(context + "-" + section + " horizontal bounds", () =>
                        {
                            foreach (var control in Descendants(window).OfType<Control>().Where(c => c.IsVisible && c is ButtonBase or TextBox or ListBox or ComboBox))
                                AssertHorizontalBounds(control, window);
                        });
                        if (section == "Library")
                        {
                            Check(context + " library columns", () => AssertLibraryGeometry(window));
                            Check(context + " library selection scope", () => AssertSelection(window, model.LibraryPage, "LibraryPage.SelectionState"));
                            Check(context + " search keyboard and empty scope", () => AssertSearchAndEmpty(window, model));
                        }
                        if (section == "Collect")
                        {
                            Check(context + " collect workspace geometry", () =>
                            {
                                collectSizes[(theme, size.Width, size.Height)] = AssertCollectGeometry(window, size, context);
                            });
                            Check(context + " pending names", () =>
                            {
                                var labels = Descendants(window).OfType<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToArray();
                                Assert.Contains(labels, text => text.StartsWith("嗅探名称验证号，", StringComparison.Ordinal));
                                Assert.Contains("名称回归测试文章0", labels);
                                Assert.Contains(labels, text => text.Contains($"本次目录页 {model.CollectPage.PageCount}", StringComparison.Ordinal) &&
                                                               text.Contains($"待保存 {model.CollectPage.PendingCount}", StringComparison.Ordinal));
                            });
                            Check(context + " pending selection scope", () => AssertSelection(window, model.PendingPage, "PendingPage.SelectionState"));
                            Check(context + " account filter and retained selection", () => AssertPendingAccountFilter(window, model));
                            Check(context + " independent scrolling and fixed footer", () => AssertCollectScrolling(window, model, root, context));
                        }
                        if (section == "Accounts")
                            Check(context + " account actions", () =>
                            {
                                var name = Descendants(window).OfType<TextBlock>().Single(t => t.IsVisible && t.Text == "嗅探名称验证号");
                                Assert.True(name.ActualWidth >= 200, $"Account name width {name.ActualWidth}.");
                                var row = Ancestors(name).OfType<Grid>().First();
                                if (window.IsCompactLayout)
                                {
                                    var actions = Descendants(row).OfType<WrapPanel>().Single();
                                    AssertPixelAligned(Bounds(name, window).Left, Bounds(actions, window).Left, window);
                                    Assert.DoesNotContain(Descendants(window).OfType<TextBlock>(), label => label.IsVisible && label.Text == "操作");
                                }
                                foreach (var button in Descendants(row).OfType<Button>().Where(b => b.IsVisible))
                                {
                                    Assert.Contains("嗅探名称验证号", PeerName(button));
                                    Assert.False(Bounds(name, row).IntersectsWith(Bounds(button, row)), "Account name overlaps an action.");
                                }
                            });
                    }
                }
                foreach (var theme in new[] { "Light", "Dark" })
                foreach (var shape in new[] { (Width: 1000d, Height: 1000d), (Width: 1280d, Height: 840d) })
                    Check($"{theme}-{shape.Width} content height growth", () =>
                    {
                        Assert.True(collectSizes.TryGetValue((theme, shape.Width, 640), out var shortSize), "Missing short-window geometry.");
                        Assert.True(collectSizes.TryGetValue((theme, shape.Width, shape.Height), out var tallSize), "Missing tall-window geometry.");
                        var growth = tallSize.Window - shortSize.Window;
                        Assert.True(growth > 100, $"Actual window height did not grow: {growth} DIP.");
                        AssertCollectAligned(growth, tallSize.Content - shortSize.Content, window, vertical: true);
                        AssertCollectAligned(growth, tallSize.Panel - shortSize.Panel, window, vertical: true);
                        AssertCollectAligned(growth, tallSize.List - shortSize.List, window, vertical: true);
                        output.WriteLine($"{theme}-{shape.Width} growth DIP: window={growth:F2}, content={tallSize.Content - shortSize.Content:F2}, articles={tallSize.Panel - shortSize.Panel:F2}, list={tallSize.List - shortSize.List:F2}.");
                    });
                model.Section = "Library";
                var emptyModel = new MainViewModel(new Library(Path.Combine(root, "empty-library")), Path.Combine(root, "empty-root"), null);
                emptyModel.Initialize();
                emptyModel.Section = "Library";
                window.DataContext = emptyModel;
                Settle(window);
                Check("true empty library action", () =>
                {
                    Assert.Single(Descendants(window).OfType<Button>(), b => b.IsVisible && b.Content is "获取目录");
                    Assert.DoesNotContain(Descendants(window).OfType<Button>(), b => b.IsVisible && b.Content is "清除筛选");
                    Assert.All(Descendants(window).OfType<CheckBox>().Where(c => c.IsVisible), c => Assert.False(c.IsEnabled));
                });
                Capture(window, root, "empty-library");
                emptyModel.Pending.Merge("pending-only", "只有目录的测试号",
                    [new PendingArticle("only-item", "pending-only", "只有目录的测试号", "待保存测试文章", "",
                        "https://mp.weixin.qq.com/s/pending-only", time, time)], 1, true);
                emptyModel.Initialize();
                emptyModel.LibraryPage.ShowAll = true;
                Settle(window);
                Capture(window, root, "pending-only-library");
                Check("pending-only library action", () =>
                {
                    Assert.DoesNotContain(Descendants(window).OfType<Button>(), b => b.IsVisible && b.Content is "获取目录");
                    var pending = Descendants(window).OfType<Button>().Single(b => b.IsVisible && b.Content is "查看待保存");
                    pending.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Settle(window);
                    Assert.True(emptyModel.LibraryPage.ShowPending);
                    Assert.False(emptyModel.LibraryPage.IsEmpty);
                });
                output.WriteLine("DPI: " + VisualTreeHelper.GetDpi(window).PixelsPerDip);
                output.WriteLine("Artifacts: " + root);
                Check("maximized client area", () => AssertMaximizedClientArea(app, root));
                Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
            }
            catch (Exception ex) { failure = ex; }
            finally { window?.Close(); app?.Shutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "WPF smoke check timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void AssertAutomation(MainWindow window)
    {
        foreach (var input in Descendants(window).OfType<Control>().Where(c => c.IsVisible &&
                     (c is RadioButton or CheckBox or TextBox or ListBox or ComboBox || c is Button { Command: not null })))
        {
            var name = PeerName(input);
            Assert.False(string.IsNullOrWhiteSpace(name) || name.StartsWith("System.", StringComparison.Ordinal), $"Unreadable automation name: {input.GetType().Name}, {name}");
            if (input is CheckBox && input.DataContext is ArticleRow article)
                Assert.Equal("选择文章：" + article.Title, name);
            if (input is CheckBox && input.DataContext is PendingArticleRow pending)
                Assert.Equal("选择文章：" + pending.Title, name);
        }
        var navigation = Descendants(window).OfType<RadioButton>().Where(r => r.GroupName == "Nav").Select(PeerName).ToArray();
        Assert.Equal(new[] { "资料库", "采集", "公众号" }, navigation);
    }

    private void AssertMaximizedClientArea(Application app, string root)
    {
        var model = new MainViewModel(new Library(Path.Combine(root, "maximize-library")), Path.Combine(root, "maximize-root"), null);
        model.Initialize();
        var window = new MainWindow(model) { ShowInTaskbar = false };
        var failures = new List<string>();
        try
        {
            // Keep constructor size limits intact so this window catches stale WorkArea constraints.
            window.Show();
            foreach (var theme in new[] { "Light", "Dark" })
            {
                app.Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri($"/WenCun;component/Themes/{theme}.xaml", UriKind.Relative) };
                WindowTheme.ApplyTextRendering(window, theme == "Dark");
                for (var cycle = 1; cycle <= 2; cycle++)
                foreach (var state in new[] { WindowState.Maximized, WindowState.Normal })
                {
                    window.WindowState = state;
                    Settle(window);
                    Assert.True(GetClientRect(new WindowInteropHelper(window).Handle, out var client), $"GetClientRect failed: {Marshal.GetLastWin32Error()}.");
                    var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                    var dpi = VisualTreeHelper.GetDpi(content);
                    var widthGap = client.Right - client.Left - content.ActualWidth * dpi.DpiScaleX;
                    var heightGap = client.Bottom - client.Top - content.ActualHeight * dpi.DpiScaleY;
                    var metrics = $"{theme} {state} cycle {cycle}: HWND client={client.Right - client.Left}x{client.Bottom - client.Top}px, content={content.ActualWidth:F2}x{content.ActualHeight:F2} DIP, DPI={dpi.PixelsPerInchX:F0}x{dpi.PixelsPerInchY:F0}, uncovered right={widthGap:F3}px bottom={heightGap:F3}px, constructor MaxWidth={window.MaxWidth} MaxHeight={window.MaxHeight}.";
                    output.WriteLine(metrics);
                    if (Math.Abs(widthGap) > 1.00001 || Math.Abs(heightGap) > 1.00001) failures.Add(metrics);
                }
            }
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }
        finally { window.Close(); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private (double Window, double Content, double Panel, double List) AssertCollectGeometry(MainWindow window, Size requested, string context)
    {
        var root = Named<Grid>(window, "CollectPageRoot");
        var workspace = Named<Grid>(window, "CollectWorkspace");
        var articles = Named<Border>(window, "ArticleSelectionPanel");
        var settings = Named<Border>(window, "SaveSettingsPanel");
        var list = Named<ScrollViewer>(window, "PendingArticleScroll");
        var settingsScroll = Named<ScrollViewer>(window, "SaveSettingsScroll");
        var save = Named<Button>(window, "PendingSaveButton");
        var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
        var articleBounds = Bounds(articles, window);
        var settingsBounds = Bounds(settings, window);
        output.WriteLine($"{context} actual DIP: window={window.ActualWidth:F2}x{window.ActualHeight:F2}, client={content.ActualWidth:F2}x{content.ActualHeight:F2}, collect={root.ActualWidth:F2}x{root.ActualHeight:F2}, workspace={workspace.ActualWidth:F2}x{workspace.ActualHeight:F2}, articles={articleBounds}, settings={settingsBounds}, listViewport={list.ViewportHeight:F2}.");
        AssertCollectAligned(requested.Width, window.ActualWidth, window);
        AssertCollectAligned(requested.Height, window.ActualHeight, window, vertical: true);
        Assert.Equal(2, root.RowDefinitions.Count);
        Assert.True(root.RowDefinitions[0].Height.IsAuto);
        Assert.True(root.RowDefinitions[1].Height.IsStar);
        Assert.DoesNotContain(Ancestors(workspace), parent => parent is ScrollViewer or StackPanel);
        Assert.Contains(root, Ancestors(workspace));
        Assert.Contains(workspace, Ancestors(articles));
        Assert.Contains(workspace, Ancestors(settings));
        AssertCollectAligned(articleBounds.Top, settingsBounds.Top, window, vertical: true);
        AssertCollectAligned(articleBounds.Bottom, settingsBounds.Bottom, window, vertical: true);
        Assert.True(articleBounds.Right < settingsBounds.Left, "The article and settings panels must remain side by side.");
        Assert.True(articles.ActualWidth > settings.ActualWidth * 1.35,
            $"Article width {articles.ActualWidth} must exceed 1.35 times settings width {settings.ActualWidth}.");
        AssertCollectAligned(Math.Clamp(workspace.ActualWidth * .28, 248, 288), settings.ActualWidth, window);
        Assert.True(double.IsNaN(articles.Height), "The article panel must use available height, not a fixed Height.");
        Assert.True(list.ViewportHeight > 0 && list.ScrollableHeight > 0, "The fixture must overflow the independent article viewport at every size.");
        Assert.Contains(articles, Ancestors(list));
        Assert.Contains(settings, Ancestors(settingsScroll));
        Assert.Contains(settings, Ancestors(save));
        Assert.DoesNotContain(Ancestors(save), parent => parent is ScrollViewer);
        AssertInside(articles, root);
        AssertInside(settings, root);
        AssertInside(root, content);
        AssertInside(save, settings);
        AssertInside(list, articles);
        AssertInside(settingsScroll, settings);
        foreach (var text in Descendants(root).OfType<TextBlock>().Where(t => t.IsVisible &&
                     !Ancestors(t).OfType<ScrollViewer>().Any()))
        {
            AssertInside(text, root);
            AssertTextFits(text);
        }
        return (window.ActualHeight, content.ActualHeight, articles.ActualHeight, list.ViewportHeight);
    }

    private static void AssertPendingAccountFilter(MainWindow window, MainViewModel model)
    {
        var filter = Named<ComboBox>(window, "PendingAccountFilter");
        var page = model.PendingPage;
        Assert.Equal("PendingPage.AccountChoices", BindingOperations.GetBinding(filter, ItemsControl.ItemsSourceProperty)?.Path.Path);
        Assert.Equal("PendingPage.AccountFilter", BindingOperations.GetBinding(filter, Selector.SelectedItemProperty)?.Path.Path);
        Assert.Equal(BindingMode.TwoWay, BindingOperations.GetBinding(filter, Selector.SelectedItemProperty)?.Mode);
        Assert.Equal(page.Accounts.Count + 1, filter.Items.Count);
        Assert.Equal(0, filter.SelectedIndex);
        Assert.Null(page.SelectedAccount);
        var total = page.Items.Count;
        try
        {
            filter.IsDropDownOpen = true;
            Settle(window);
            var options = Enumerable.Range(0, filter.Items.Count)
                .Select(index => Assert.IsType<ComboBoxItem>(filter.ItemContainerGenerator.ContainerFromIndex(index))).ToArray();
            var selected = page.Items.First(row => row.Article.Biz == page.Accounts[0].Account.Biz);
            selected.Selected = true;
            var peer = Assert.IsType<ComboBoxAutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(filter))
                .GetChildren().OfType<SelectorItemAutomationPeer>().Single(p => ReferenceEquals(p.Item, filter.Items[1]));
            Assert.IsAssignableFrom<ISelectionItemProvider>(peer.GetPattern(PatternInterface.SelectionItem)).Select();
            filter.IsDropDownOpen = false;
            Settle(window);
            var account = Assert.IsType<PendingAccountRow>(page.SelectedAccount);
            Assert.Same(page.Accounts[0], account);
            Assert.Equal(account.Account.Articles.Count, page.Items.Count);
            Assert.All(page.Items, row => Assert.Equal(account.Account.Biz, row.Article.Biz));
            Assert.True(page.Items.Single(row => row.Article.Id == selected.Article.Id).Selected);
            Assert.Equal(1, page.SelectedCount);
            var biz = account.Account.Biz;
            page.Load();
            Settle(window);
            Assert.Equal(biz, page.SelectedAccount?.Account.Biz);
            Assert.Equal(1, filter.SelectedIndex);
            Assert.Equal(1, page.SelectedCount);
            AssertFilterText();
            filter.Focus();
            filter.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(filter), 0, Key.F4)
                { RoutedEvent = Keyboard.KeyDownEvent });
            Settle(window);
            Assert.True(filter.IsDropDownOpen, "F4 must open the account filter.");
            filter.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(filter), 0, Key.Escape)
                { RoutedEvent = Keyboard.KeyDownEvent });
            Settle(window);
            Assert.False(filter.IsDropDownOpen, "Escape must close the account filter.");
            Assert.Equal(biz, page.SelectedAccount?.Account.Biz);
            Assert.Equal(1, filter.SelectedIndex);
            page.SelectAccount(page.Accounts[1].Account.Biz);
            Settle(window);
            Assert.Equal(2, filter.SelectedIndex);
            AssertFilterText();
            filter.IsDropDownOpen = true;
            Settle(window);
            var all = Assert.IsType<ComboBoxAutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(filter))
                .GetChildren().OfType<SelectorItemAutomationPeer>().Single(p => ReferenceEquals(p.Item, filter.Items[0]));
            Assert.IsAssignableFrom<ISelectionItemProvider>(all.GetPattern(PatternInterface.SelectionItem)).Select();
            filter.IsDropDownOpen = false;
            Settle(window);
            Assert.Null(page.SelectedAccount);
            Assert.Equal(total, page.Items.Count);
            Assert.Equal(0, filter.SelectedIndex);
            Assert.Contains(Descendants(filter).OfType<TextBlock>(), t => t.IsVisible && t.Text == "全部公众号");
            filter.IsDropDownOpen = true;
            Settle(window);
            options = Enumerable.Range(0, filter.Items.Count)
                .Select(index => Assert.IsType<ComboBoxItem>(filter.ItemContainerGenerator.ContainerFromIndex(index))).ToArray();
            Assert.Equal("全部公众号", PeerName(options[0]));
            Assert.Equal(page.Accounts.Select(a => a.Name), options.Skip(1).Select(PeerName));
            Assert.Contains("嗅探名称验证号", options.Select(PeerName));
        }
        finally
        {
            filter.IsDropDownOpen = false;
            page.SelectAllCommand.Execute(null);
            page.AllSelected = false;
            Settle(window);
        }

        void AssertFilterText()
        {
            var text = Descendants(filter).OfType<TextBlock>().Single(t => t.IsVisible && t.Text == page.SelectedAccount!.Name);
            AssertInside(text, filter);
            AssertTextFits(text);
        }
    }

    private void AssertCollectScrolling(MainWindow window, MainViewModel model, string root, string context)
    {
        var articles = Named<Border>(window, "ArticleSelectionPanel");
        var settings = Named<Border>(window, "SaveSettingsPanel");
        var list = Named<ScrollViewer>(window, "PendingArticleScroll");
        var settingsScroll = Named<ScrollViewer>(window, "SaveSettingsScroll");
        var save = Named<Button>(window, "PendingSaveButton");
        var selection = Descendants(articles).OfType<CheckBox>().Single(c => c.Content is "全选当前列表");
        list.ScrollToTop();
        settingsScroll.ScrollToTop();
        Settle(window);
        try
        {
            Assert.False(save.IsEnabled);
            ClickWithKeyboard(selection);
            Settle(window);
            Assert.Equal(model.PendingPage.Items.Count, model.PendingPage.SelectedCount);
            Assert.True(save.IsEnabled);
            Assert.Equal(model.PendingPage.SaveText, save.Content);
            Assert.Equal(model.PendingPage.SaveText, PeerName(save));
            var count = Descendants(Named<Border>(window, "PendingSelectionFooter")).OfType<TextBlock>()
                .Single(t => t.IsVisible && t.Text == $"已选 {model.PendingPage.SelectedCount} 篇");
            var remove = Descendants(articles).OfType<Button>().Single(b => b.Command == model.PendingPage.DeleteCommand);
            var articleFooter = PanelRegion(count, articles);
            var settingsFooter = PanelRegion(save, settings);
            Assert.DoesNotContain(Ancestors(count), parent => parent is ScrollViewer);
            Assert.DoesNotContain(Ancestors(remove), parent => parent is ScrollViewer);
            AssertInside(count, articleFooter);
            AssertInside(remove, articleFooter);
            AssertInside(save, settingsFooter);
            Assert.True(Bounds(count, articleFooter).Left + count.DesiredSize.Width <= Bounds(remove, articleFooter).Left,
                "The selection count overlaps the remove action.");
            AssertCollectAligned(Bounds(articleFooter, window).Bottom, Bounds(settingsFooter, window).Bottom, window, vertical: true);
            Assert.True(Bounds(list, articles).Bottom <= Bounds(articleFooter, articles).Top + 1);
            Assert.True(Bounds(settingsScroll, settings).Bottom <= Bounds(settingsFooter, settings).Top + 1);
            var fixedElements = new FrameworkElement[] { save, count, remove, selection, articleFooter, settingsFooter };
            var fixedBounds = fixedElements.Select(element => Bounds(element, window)).ToArray();
            foreach (var element in fixedElements)
            {
                AssertInside(element, window);
                foreach (var text in Descendants(element).OfType<TextBlock>()) AssertTextFits(text);
            }
            AssertTextFits(count);
            var rows = Descendants(list).OfType<CheckBox>().Where(c => c.DataContext is PendingArticleRow).ToArray();
            Assert.Equal(model.PendingPage.Items.Count, rows.Length);
            AssertRowReachable(rows[0]);
            AssertCollectAligned(0, list.VerticalOffset, window, vertical: true);
            list.ScrollToBottom();
            Settle(window);
            AssertRowReachable(rows[^1]);
            Assert.True(list.VerticalOffset > 0);
            AssertCollectAligned(list.ScrollableHeight, list.VerticalOffset, window, vertical: true);
            AssertCollectAligned(0, settingsScroll.VerticalOffset, window, vertical: true);
            AssertFixed();
            var articleOffset = list.VerticalOffset;
            var certificate = Descendants(settings).OfType<Button>().Single(b => b.Content is "证书管理");
            certificate.BringIntoView();
            Settle(window);
            ClickWithKeyboard(certificate);
            Settle(window);
            Assert.True(model.CollectPage.ShowCertificate);
            var removeCertificate = Descendants(settings).OfType<Button>().Single(b => b.Content is "移除本地证书");
            Assert.Contains(settingsScroll, Ancestors(removeCertificate));
            settingsScroll.ScrollToBottom();
            removeCertificate.BringIntoView();
            removeCertificate.Focus();
            Settle(window);
            AssertInside(removeCertificate, settings);
            AssertInside(removeCertificate, Descendants(settingsScroll).OfType<ScrollContentPresenter>().First());
            Assert.True(removeCertificate.IsKeyboardFocused);
            Assert.Equal("移除本地证书", PeerName(removeCertificate));
            AssertCollectAligned(settingsScroll.ScrollableHeight, settingsScroll.VerticalOffset, window, vertical: true);
            if (window.Height == 640) Assert.True(settingsScroll.ScrollableHeight > 0, "Expanded settings must exercise scrolling in short windows.");
            AssertCollectAligned(articleOffset, list.VerticalOffset, window, vertical: true);
            AssertFixed();
            foreach (var input in Descendants(settings).OfType<ToggleButton>().Where(c => c is CheckBox or RadioButton))
                Assert.Contains(Ancestors(input).OfType<FrameworkElement>(), parent =>
                    BindingOperations.GetBinding(parent, UIElement.IsEnabledProperty)?.Path.Path == "CollectPage.CanEditSettings");
            output.WriteLine($"{context} independent scroll DIP: articles={list.VerticalOffset:F2}/{list.ScrollableHeight:F2}, settings={settingsScroll.VerticalOffset:F2}/{settingsScroll.ScrollableHeight:F2}, fixedSave={Bounds(save, window)}, leftFooter={Bounds(articleFooter, window)}, rightFooter={Bounds(settingsFooter, window)}.");
            Capture(window, root, context + "-certificate-expanded");
            settingsScroll.ScrollToTop();
            Settle(window);
            var html = Descendants(settingsScroll).OfType<CheckBox>().Single(c => c.Content is "HTML");
            AssertInside(html, Descendants(settingsScroll).OfType<ScrollContentPresenter>().First());
            AssertCollectAligned(articleOffset, list.VerticalOffset, window, vertical: true);
            list.ScrollToTop();
            Settle(window);
            AssertRowReachable(rows[0]);
            AssertFixed();

            void AssertFixed()
            {
                for (var i = 0; i < fixedElements.Length; i++)
                {
                    var actual = Bounds(fixedElements[i], window);
                    AssertCollectAligned(fixedBounds[i].Left, actual.Left, window);
                    AssertCollectAligned(fixedBounds[i].Right, actual.Right, window);
                    AssertCollectAligned(fixedBounds[i].Top, actual.Top, window, vertical: true);
                    AssertCollectAligned(fixedBounds[i].Bottom, actual.Bottom, window, vertical: true);
                }
            }

            void AssertRowReachable(CheckBox row)
            {
                row.BringIntoView();
                row.Focus();
                Settle(window);
                var viewport = Descendants(list).OfType<ScrollContentPresenter>().First();
                AssertInside(row, viewport);
                Assert.True(row.IsKeyboardFocused);
                var title = Descendants(list).OfType<TextBlock>().Single(t => t.DataContext == row.DataContext && t.Text == ((PendingArticleRow)row.DataContext).Title);
                AssertInside(title, viewport);
                AssertTextFits(title);
            }
        }
        finally
        {
            if (model.CollectPage.ShowCertificate) model.CollectPage.ToggleCertificateCommand.Execute(null);
            model.PendingPage.AllSelected = false;
            list.ScrollToTop();
            settingsScroll.ScrollToTop();
            Settle(window);
        }
    }

    private static T Named<T>(MainWindow window, string name) where T : FrameworkElement
    {
        var element = window.FindName(name);
        Assert.True(element is T, $"Missing {typeof(T).Name} named {name}.");
        return (T)element;
    }

    private static FrameworkElement PanelRegion(FrameworkElement element, Border panel)
    {
        var grid = Assert.IsType<Grid>(panel.Child);
        return Ancestors(element).Prepend(element).OfType<FrameworkElement>()
            .First(parent => ReferenceEquals(VisualTreeHelper.GetParent(parent), grid));
    }

    private static void AssertCollectAligned(double expected, double actual, Visual visual, bool vertical = false)
    {
        var dpi = VisualTreeHelper.GetDpi(visual);
        var pixels = Math.Abs(expected - actual) * (vertical ? dpi.DpiScaleY : dpi.DpiScaleX);
        Assert.True(pixels <= 1.00001, $"Collect alignment differs by {pixels:F3} device pixels ({expected:F3} vs {actual:F3}).");
    }

    private static void AssertInside(FrameworkElement element, FrameworkElement container)
    {
        var bounds = Bounds(element, container);
        var dpi = VisualTreeHelper.GetDpi(element);
        Assert.True(bounds.Left >= -1 / dpi.DpiScaleX && bounds.Top >= -1 / dpi.DpiScaleY &&
                    bounds.Right <= container.ActualWidth + 1 / dpi.DpiScaleX && bounds.Bottom <= container.ActualHeight + 1 / dpi.DpiScaleY,
            $"{element.GetType().Name} {element.Name} {PeerName(element)} is clipped: {bounds} inside {container.Name} {container.ActualWidth:F2}x{container.ActualHeight:F2} DIP.");
    }

    private static void AssertTextFits(TextBlock text)
    {
        if (!text.IsVisible || string.IsNullOrEmpty(text.Text)) return;
        var formatted = new FormattedText(text.Text, CultureInfo.CurrentCulture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize,
            text.Foreground, VisualTreeHelper.GetDpi(text).PixelsPerDip);
        if (text.TextWrapping != TextWrapping.NoWrap && text.ActualWidth > 0) formatted.MaxTextWidth = text.ActualWidth;
        if (!double.IsNaN(text.LineHeight)) formatted.LineHeight = text.LineHeight;
        Assert.True(formatted.WidthIncludingTrailingWhitespace <= text.ActualWidth + 1 && formatted.Height <= text.ActualHeight + 1,
            $"Text '{text.Text}' is truncated: needs {formatted.WidthIncludingTrailingWhitespace:F2}x{formatted.Height:F2}, has {text.ActualWidth:F2}x{text.ActualHeight:F2} DIP.");
        foreach (var parent in Ancestors(text).OfType<FrameworkElement>().Where(p => p.ClipToBounds)) AssertInside(text, parent);
    }

    private static void AssertSelection(MainWindow window, object page, string path)
    {
        var header = Descendants(window).OfType<CheckBox>().Single(c => c.IsVisible &&
            BindingOperations.GetBinding(c, ToggleButton.IsCheckedProperty)?.Path.Path == path);
        var scope = page is LibraryViewModel ? "全选当前页" : "全选当前列表";
        Assert.Equal(scope, PeerName(header));
        Assert.Equal(scope, header.Content);
        Assert.True(header.IsThreeState);
        Assert.Equal(path, BindingOperations.GetBinding(header, ToggleButton.IsCheckedProperty)?.Path.Path);
        Assert.Equal(BindingMode.OneWay, BindingOperations.GetBinding(header, ToggleButton.IsCheckedProperty)?.Mode);
        Assert.Equal(page is LibraryViewModel ? "LibraryPage.ToggleSelectionCommand" : "PendingPage.ToggleSelectionCommand",
            BindingOperations.GetBinding(header, ButtonBase.CommandProperty)?.Path.Path);
        var rows = page is LibraryViewModel library ? library.Items.Cast<object>().ToArray() : ((PendingViewModel)page).Items.Cast<object>().ToArray();
        var first = Descendants(window).OfType<CheckBox>().First(c => c.IsVisible && c.DataContext == rows[0]);
        first.IsChecked = true;
        Settle(window);
        Assert.Null(header.IsChecked);
        ClickWithKeyboard(header);
        Settle(window);
        Assert.All(rows, row => Assert.True(row is ArticleRow article ? article.Selected : ((PendingArticleRow)row).Selected));
        ClickWithKeyboard(header);
        Settle(window);
        Assert.All(rows, row => Assert.False(row is ArticleRow article ? article.Selected : ((PendingArticleRow)row).Selected));
    }

    private static void AssertSearchAndEmpty(MainWindow window, MainViewModel model)
    {
        var search = Descendants(window).OfType<TextBox>().Single(t => t.IsVisible);
        Assert.Equal("搜索标题、公众号、作者", PeerName(search));
        Assert.Contains(window.InputBindings.OfType<KeyBinding>(), b => b.Key == Key.F && b.Modifiers == ModifierKeys.Control && b.Command == ApplicationCommands.Find);
        model.Section = "Accounts";
        ApplicationCommands.Find.Execute(null, window);
        Settle(window);
        Assert.Equal("Library", model.Section);
        Assert.True(search.IsKeyboardFocused);
        search.Text = "没有匹配结果的测试词";
        model.LibraryPage.SearchCommand.Execute(null);
        Settle(window);
        Assert.True(model.LibraryPage.IsEmpty);
        Assert.DoesNotContain(Descendants(window).OfType<Button>(), b => b.IsVisible && b.Content is "获取目录");
        var clear = Descendants(window).OfType<Button>().Single(b => b.IsVisible && b.Content is "清除筛选");
        Assert.True(clear.Command.CanExecute(clear.CommandParameter));
        clear.Command.Execute(clear.CommandParameter);
        Settle(window);
        Assert.False(model.LibraryPage.IsEmpty);
        search.Text = "第二次无匹配测试词";
        model.LibraryPage.SearchCommand.Execute(null);
        search.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(search), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        Settle(window);
        Assert.Equal("", model.LibraryPage.Search);
        Assert.False(model.LibraryPage.IsEmpty);
        Assert.True(search.IsKeyboardFocused);
    }

    private static void AssertLibraryGeometry(MainWindow window)
    {
        var titles = Descendants(window).OfType<TextBlock>().Where(t => t.IsVisible).ToArray();
        var failedTitle = titles.Single(t => t.Text == "保存失败后仍可阅读的文章");
        var readyTitle = titles.Single(t => t.Text == "保存成功的文章");
        Assert.True(failedTitle.ActualWidth >= 200, $"Article title width {failedTitle.ActualWidth}.");
        var failedRow = Ancestors(failedTitle).OfType<Grid>().First(g => g.ColumnDefinitions.Count == 4);
        var readyRow = Ancestors(readyTitle).OfType<Grid>().First(g => g.ColumnDefinitions.Count == 4);
        var header = Descendants(window).OfType<CheckBox>().Single(c => c.IsVisible && c.DataContext == window.DataContext);
        var selected = Descendants(failedRow).OfType<CheckBox>().Single();
        AssertPixelAligned(Bounds(selected, window).X, Bounds(header, window).X, window);
        var failedState = Descendants(failedRow).OfType<TextBlock>().Single(t => t.Text == "采集失败");
        var readyState = Descendants(readyRow).OfType<TextBlock>().Single(t => t.Text == "已保存");
        var failedPill = Ancestors(failedState).OfType<Border>().First();
        var readyPill = Ancestors(readyState).OfType<Border>().First();
        var stateHeader = titles.Single(t => t.Text == "状态");
        AssertPixelAligned(Center(failedPill, window).X, Center(readyPill, window).X, window);
        AssertPixelAligned(Center((FrameworkElement)failedPill.Parent, window).X, Center(failedPill, window).X, window);
        AssertPixelAligned(Center((FrameworkElement)readyPill.Parent, window).X, Center(readyPill, window).X, window);
        AssertPixelAligned(Center(failedPill, window).X, Center(stateHeader, window).X, window);
        var actions = Descendants(failedRow).OfType<Button>().Where(b => b.IsVisible).ToArray();
        Assert.Equal(new[] { "阅读", "重试", "文件夹" }, actions.Select(b => b.Content));
        Assert.All(actions, button =>
        {
            Assert.True(button.ActualWidth >= 40);
            Assert.Contains(failedTitle.Text, PeerName(button));
        });
        Assert.True(Bounds(actions[0], window).Right <= Bounds(actions[1], window).Left);
        Assert.True(Bounds(actions[1], window).Right <= Bounds(actions[2], window).Left);
        AssertPixelAligned(Center(failedPill, window).Y, Center(actions[0], window).Y, window);
        var readyRead = Descendants(readyRow).OfType<Button>().Single(b => b.IsVisible && b.Content is "阅读");
        AssertPixelAligned(Center(readyPill, window).Y, Center(readyRead, window).Y, window);
    }

    private static void ClickWithKeyboard(ButtonBase checkBox)
    {
        checkBox.Focus();
        var source = PresentationSource.FromVisual(checkBox);
        checkBox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Space) { RoutedEvent = Keyboard.KeyDownEvent });
        checkBox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Space) { RoutedEvent = Keyboard.KeyUpEvent });
    }

    private void Capture(MainWindow window, string root, string name)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(root, name + ".png");
        using var image = File.Create(path);
        encoder.Save(image);
        output.WriteLine("Screenshot: " + path);
        output.WriteLine($"RenderTargetBitmap: actual window {window.ActualWidth:F2}x{window.ActualHeight:F2} DIP; local WorkArea {SystemParameters.WorkArea.Width:F2}x{SystemParameters.WorkArea.Height:F2} DIP; DPI {dpi.PixelsPerInchX:F0}x{dpi.PixelsPerInchY:F0}. Areas outside the local visible desktop are RenderTargetBitmap evidence, not on-screen visibility evidence.");
    }

    private static void AssertPixelAligned(double expected, double actual, Visual visual)
    {
        var pixels = Math.Abs(expected - actual) * VisualTreeHelper.GetDpi(visual).DpiScaleX;
        Assert.True(pixels <= 0.50001, $"Alignment differs by {pixels:F3} device pixels ({expected:F3} vs {actual:F3}).");
    }

    private static string PeerName(UIElement element) => UIElementAutomationPeer.CreatePeerForElement(element)?.GetName() ?? "";
    private static void Settle(Window window)
    {
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }
    private static Rect Bounds(FrameworkElement element, Visual relativeTo) => element.TransformToAncestor(relativeTo).TransformBounds(new Rect(element.RenderSize));
    private static void AssertHorizontalBounds(FrameworkElement element, Window window)
    {
        var bounds = Bounds(element, window);
        Assert.True(bounds.Left >= -1 && bounds.Right <= window.ActualWidth + 1, $"{element.GetType().Name} {PeerName(element)} outside window: {bounds}.");
        foreach (var parent in Ancestors(element).OfType<FrameworkElement>().Where(p => p.ClipToBounds))
        {
            var inside = Bounds(element, parent);
            Assert.True(inside.Left >= -1 && inside.Right <= parent.ActualWidth + 1, $"{PeerName(element)} clipped horizontally: {inside}, parent {parent.ActualWidth}.");
        }
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
        var bounds = Bounds(element, relativeTo);
        return new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
    }
}
