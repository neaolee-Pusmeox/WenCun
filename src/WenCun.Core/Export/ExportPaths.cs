using System.Security.Cryptography;
using System.Text;
using WenCun.Core.Content;
using WenCun.Core.Domain;

namespace WenCun.Core.Export;

public static class ExportPaths
{
    public static string FolderFor(string articlesRoot, Article article) =>
        Path.Combine(articlesRoot,
            Component(article.Account.Length > 0 ? article.Account : "未命名公众号"),
            (article.PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd") ?? "日期未知") + " " + Component(article.Title) +
            " [" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(article.Id)))[..16] + "]");

    public static string LegacyFolderFor(string articlesRoot, Article article) =>
        Path.Combine(articlesRoot,
            UrlRules.SafeFileName(article.Account.Length > 0 ? article.Account : "未命名公众号"),
            (article.PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd") ?? "日期未知") + " " + UrlRules.SafeFileName(article.Title));

    private static string Component(string name)
    {
        var value = UrlRules.SafeFileName(name).TrimEnd('.', ' ');
        if (value.Length == 0) return "未命名";
        var device = value.Split('.')[0].ToUpperInvariant();
        if (device is "CON" or "PRN" or "AUX" or "NUL" ||
            (device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) &&
             device[3] is >= '1' and <= '9')) value = "_" + value;
        return value;
    }
}
