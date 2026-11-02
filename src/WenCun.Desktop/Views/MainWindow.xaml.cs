using System.Windows;
using Microsoft.Win32;
using WenCun.Desktop.ViewModels;

namespace WenCun.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel model)
    {
        InitializeComponent();
        var area = SystemParameters.WorkArea;
        MaxWidth = area.Width;
        MaxHeight = area.Height;
        Width = Math.Min(Width, area.Width);
        Height = Math.Min(Height, area.Height);
        DataContext = model;
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
            new ReaderWindow(article, content, model.Library) { Owner = this }.Show();
        model.ChangeLibraryRequested += () =>
        {
            var dialog = new OpenFolderDialog { Title = "选择文库文件夹", InitialDirectory = model.LibraryPath };
            if (dialog.ShowDialog(this) == true) model.ChangeLibrary(dialog.FolderName);
        };
    }
}
