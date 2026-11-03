using System.IO;
using System.Windows;
using WenCun.Capture;
using WenCun.Core.Storage;
using WenCun.Desktop.Infrastructure;
using WenCun.Desktop.ViewModels;
using WenCun.Desktop.Views;

namespace WenCun.Desktop;

public partial class App : Application
{
    public static string AppRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WenCun");

    public static string Version => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static string BuildStamp
    {
        get
        {
            try
            {
                var path = Environment.ProcessPath;
                return path is null ? "" : File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
        }
    }

    public static AppConfig Config { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Directory.CreateDirectory(AppRoot);
        Config = AppConfig.Load(AppRoot);
        SetTheme(Config.Dark);
        var recovery = CaptureJournal.Recover(AppRoot);
        var libraryPath = string.IsNullOrWhiteSpace(Config.LibraryPath)
            ? Path.Combine(AppRoot, "library")
            : Config.LibraryPath;
        var library = new Library(libraryPath);
        var model = new MainViewModel(library, AppRoot, recovery);
        var window = new MainWindow(model);
        MainWindow = window;
        window.Show();
        model.Initialize();
    }

    public static void SetTheme(bool dark)
    {
        var dictionaries = Current.Resources.MergedDictionaries;
        var existing = dictionaries.FirstOrDefault(dictionary =>
            dictionary.Source?.OriginalString is { } source &&
            (source.EndsWith("Themes/Light.xaml", StringComparison.Ordinal) ||
             source.EndsWith("Themes/Dark.xaml", StringComparison.Ordinal)));
        if (existing is not null) dictionaries.Remove(existing);
        dictionaries.Insert(0, new ResourceDictionary
        {
            Source = new Uri($"Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative)
        });
        Config.Dark = dark;
        Config.Save(AppRoot);
        foreach (var window in Current.Windows.OfType<Window>())
        {
            WindowTheme.ApplyDarkTitleBar(window, dark);
            WindowTheme.ApplyTextRendering(window, dark);
        }
    }
}
