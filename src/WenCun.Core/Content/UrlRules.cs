using System.Net;
using System.Security.Cryptography;
using System.Text;
using WenCun.Core.Domain;

namespace WenCun.Core.Content;

public static class UrlRules
{
    private static readonly HashSet<string> AssetHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp.weixin.qq.com", "mmbiz.qpic.cn", "mmbiz.qlogo.cn", "res.wx.qq.com", "wx.qlogo.cn"
    };

    public static Uri RequireWeChat(string address)
    {
        var uri = RequireHttps(address);
        if (!uri.Host.Equals("mp.weixin.qq.com", StringComparison.OrdinalIgnoreCase))
            throw new WenCunException(FailureKind.Input, "只接受 mp.weixin.qq.com 的公众号地址。");
        return uri;
    }

    public static Uri RequireAsset(string address)
    {
        var uri = RequireHttps(address);
        if (!AssetHosts.Contains(uri.Host))
            throw new WenCunException(FailureKind.Input, "图片地址不在微信资源域名内。");
        return uri;
    }

    private static Uri RequireHttps(string address)
    {
        address = WebUtility.HtmlDecode(address).Trim();
        if (address.StartsWith("//", StringComparison.Ordinal)) address = "https:" + address;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.HostNameType != UriHostNameType.Dns)
            throw new WenCunException(FailureKind.Input, "需要标准的 HTTPS 地址。");
        return uri;
    }

    public static Dictionary<string, string> Query(Uri uri)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            result[Uri.UnescapeDataString(pair[0].Replace('+', ' '))] = pair.Length == 2
                ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "";
        }
        return result;
    }

    public static string CanonicalArticleUrl(string address)
    {
        if (address.StartsWith('/')) address = "https://mp.weixin.qq.com" + address;
        if (Uri.TryCreate(WebUtility.HtmlDecode(address), UriKind.Absolute, out var old) &&
            old.Scheme == "http" && old.Host == "mp.weixin.qq.com" && old.IsDefaultPort && old.UserInfo.Length == 0)
            address = new UriBuilder(old) { Scheme = "https", Port = -1 }.Uri.AbsoluteUri;
        var uri = RequireWeChat(address);
        if (uri.AbsolutePath != "/s" && !uri.AbsolutePath.StartsWith("/s/", StringComparison.Ordinal))
            throw new WenCunException(FailureKind.Input, "请输入公众号文章链接（/s 或 /s/…）。");
        if (uri.AbsolutePath != "/s") return "https://mp.weixin.qq.com" + uri.AbsolutePath;
        var query = Query(uri);
        var keys = new[] { "__biz", "mid", "idx", "sn" };
        var values = keys.Where(query.ContainsKey).Select(k => $"{k}={Uri.EscapeDataString(query[k])}").ToArray();
        if (!query.ContainsKey("__biz") || !query.ContainsKey("mid") || !query.ContainsKey("idx"))
            throw new WenCunException(FailureKind.Input, "文章链接缺少 __biz、mid 或 idx。");
        if (query["__biz"].Length is < 1 or > 512 || query["mid"].Length == 0 || !query["mid"].All(char.IsAsciiDigit) ||
            !int.TryParse(query["idx"], out var index) || index <= 0)
            throw new WenCunException(FailureKind.Input, "文章链接中的标识字段无效。");
        return "https://mp.weixin.qq.com/s?" + string.Join('&', values);
    }

    public static string ArticleId(string canonicalUrl)
    {
        var uri = new Uri(canonicalUrl);
        var query = Query(uri);
        var identity = query.TryGetValue("__biz", out var biz) && query.TryGetValue("mid", out var mid) && query.TryGetValue("idx", out var idx)
            ? $"{biz}|{mid}|{idx}"
            : uri.GetLeftPart(UriPartial.Path);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? ' ' : c).ToArray()).Trim();
        cleaned = string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (cleaned.Length == 0) cleaned = "未命名";
        return cleaned.Length <= 80 ? cleaned : cleaned[..80];
    }

}
