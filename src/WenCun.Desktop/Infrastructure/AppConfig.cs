using System.IO;
using System.Text.Json;
using WenCun.Core.Domain;

namespace WenCun.Desktop.Infrastructure;

public sealed class AppConfig
{
    public string LibraryPath { get; set; } = "";
    public bool Dark { get; set; }
    public string[] Formats { get; set; } = ["Html"];
    public string Images { get; set; } = "Download";

    public SaveOptions ToSaveOptions()
    {
        var formats = (Formats ?? [])
            .Select(name => Enum.TryParse<ExportFormat>(name, true, out var format) && Enum.IsDefined(format) ? format : (ExportFormat?)null)
            .Where(format => format is not null)
            .Select(format => format!.Value)
            .Distinct()
            .ToArray();
        var images = Enum.TryParse<ImageMode>(Images, true, out var mode) && Enum.IsDefined(mode) ? mode : ImageMode.Download;
        return new SaveOptions(formats, images);
    }

    private static string PathFor(string root) => Path.Combine(root, "config.json");

    public static AppConfig Load(string root)
    {
        try
        {
            var path = PathFor(root);
            return File.Exists(path) ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path)) ?? new AppConfig() : new AppConfig();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppConfig();
        }
    }

    public void Save(string root)
    {
        try
        {
            var path = PathFor(root);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, path, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 配置写入失败不应影响使用。
        }
    }
}
