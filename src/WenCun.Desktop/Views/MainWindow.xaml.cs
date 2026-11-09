using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using WenCun.Desktop.Infrastructure;
using WenCun.Desktop.ViewModels;

namespace WenCun.Desktop.Views;

public partial class MainWindow : Window
{
    public static readonly DependencyProperty IsCompactLayoutProperty = DependencyProperty.Register(
        nameof(IsCompactLayout), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    public bool IsCompactLayout
    {
        get => (bool)GetValue(IsCompactLayoutProperty);
        private set => SetValue(IsCompactLayoutProperty, value);
    }

    public MainWindow(MainViewModel model)
    {
        InitializeComponent();
        var area = SystemParameters.WorkArea;
        // 只约束首次打开；最大化的原生窗口包含屏外边框，固定 MaxWidth/MaxHeight 会裁掉客户区。
        Width = Math.Min(Width, area.Width);
        Height = Math.Min(Height, area.Height);
        DataContext = model;
        SizeChanged += (_, _) =>
        {
            IsCompactLayout = ActualWidth < 1180;
            MainSidebarColumn.Width = new GridLength(IsCompactLayout ? 196 : 236);
            MainContent.Margin = IsCompactLayout ? new Thickness(24, 24, 24, 0) : new Thickness(34, 28, 34, 0);
            LibraryAccountsColumn.Width = new GridLength(IsCompactLayout ? 160 : 208);
        };
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Find, (_, e) =>
        {
            model.Section = "Library";
            UpdateLayout();
            LibrarySearchTextBox.Focus();
            LibrarySearchTextBox.SelectAll();
            e.Handled = true;
        }));
        SourceInitialized += (_, _) =>
        {
            WindowTheme.ApplyDarkTitleBar(this, App.Config.Dark);
            WindowTheme.ApplyTextRendering(this, App.Config.Dark);
        };
        var closing = false;
        Closing += async (_, e) =>
        {
            if (closing) { e.Cancel = model.IsBusy; return; }
            if (model.CollectPage.IsBusy)
            {
                e.Cancel = true;
                closing = true;
                await model.CollectPage.StopAsync();
                Close();
            }
            else if (model.IsBusy)
            {
                e.Cancel = true;
                model.SetStatus("正在保存或导出，请等待当前任务结束后关闭窗口。");
            }
        };
        model.ReadRequested += (article, content) =>
            new ReaderWindow(article, content, model.Library, model.AppRoot) { Owner = this }.Show();
        model.ChangeLibraryRequested += () =>
        {
            var dialog = new OpenFolderDialog { Title = "选择文库文件夹", InitialDirectory = model.LibraryPath };
            if (dialog.ShowDialog(this) == true) model.ChangeLibrary(dialog.FolderName);
        };
        model.ConfirmPendingDeleteRequested += count => MessageBox.Show(this,
            $"移除 {count} 篇待保存文章？\n不会删除正文和已导出文件。\n重新获取目录不保证恢复这些条目。",
            "移除待保存文章", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.OK;
    }

    private void SearchPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not MainViewModel model) return;
        LibrarySearchTextBox.Clear();
        model.LibraryPage.Search = "";
        model.LibraryPage.SearchCommand.Execute(null);
        e.Handled = true;
    }

    private void CollectWorkspaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        CollectSettingsColumn.Width = new GridLength(Math.Clamp(e.NewSize.Width * 0.28, 248, 288));
    }

    private void ShowPendingArticles(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model) model.LibraryPage.ShowPending = true;
    }
}
