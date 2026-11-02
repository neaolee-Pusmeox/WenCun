using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using WenCun.Core.Domain;

namespace WenCun.Core.Content;

internal static partial class HtmlSanitizer
{
    private static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "section", "article", "span", "br", "hr", "strong", "b", "em", "i", "u", "s", "del", "ins",
        "sub", "sup", "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol", "li", "blockquote", "pre", "code",
        "a", "img", "audio", "source", "table", "thead", "tbody", "tfoot", "tr", "td", "th", "caption",
        "colgroup", "col", "figure", "figcaption"
    };

    private static readonly HashSet<string> AllowedStyles = new(StringComparer.OrdinalIgnoreCase)
    {
        "color", "background-color", "font-size", "font-weight", "font-style", "text-align", "text-decoration",
        "line-height", "margin", "margin-top", "margin-bottom", "margin-left", "margin-right", "padding",
        "padding-top", "padding-bottom", "padding-left", "padding-right", "width", "height", "max-width",
        "border", "border-collapse", "border-color", "border-width", "border-style", "vertical-align",
        "white-space", "letter-spacing", "display", "float", "list-style-type"
    };

    public static IReadOnlyList<Asset> Clean(IElement root, List<string> warnings)
    {
        var assets = new Dictionary<string, Asset>(StringComparer.Ordinal);
        foreach (var element in root.QuerySelectorAll("script,style,noscript,iframe,object,embed,form,input,button,textarea,select,meta,base,link,svg,math,video,mp-video").ToArray())
            element.Remove();
        foreach (var element in root.QuerySelectorAll("*").ToArray())
        {
            if (!AllowedTags.Contains(element.LocalName))
            {
                if (element.Parent is { } parent)
                {
                    foreach (var child in element.ChildNodes.ToArray()) parent.InsertBefore(child, element);
                    element.Remove();
                }
                continue;
            }
            var source = element.GetAttribute("data-src") ?? element.GetAttribute("src");
            var href = element.GetAttribute("href");
            var style = element.GetAttribute("style");
            var alt = element.GetAttribute("alt");
            var colspan = element.GetAttribute("colspan");
            var rowspan = element.GetAttribute("rowspan");
            foreach (var attribute in element.Attributes.ToArray()) element.RemoveAttribute(attribute.Name);
            if (alt is not null) element.SetAttribute("alt", alt);
            if (int.TryParse(colspan, out var columnSpan) && columnSpan is > 1 and <= 100) element.SetAttribute("colspan", columnSpan.ToString());
            if (int.TryParse(rowspan, out var rowSpan) && rowSpan is > 1 and <= 100) element.SetAttribute("rowspan", rowSpan.ToString());
            if (element.LocalName == "a" && Uri.TryCreate(WebUtility.HtmlDecode(href), UriKind.Absolute, out var link) &&
                link.Scheme == "https" && link.UserInfo.Length == 0)
            {
                element.SetAttribute("href", link.AbsoluteUri);
                element.SetAttribute("rel", "noreferrer noopener");
            }
            if (element.LocalName is "img" or "audio" or "source")
            {
                if (element.LocalName == "audio") element.SetAttribute("controls", "controls");
                if (string.IsNullOrWhiteSpace(source)) continue;
                try
                {
                    var uri = UrlRules.RequireAsset(source);
                    var kind = element.LocalName == "img" ? "image" : "audio";
                    element.SetAttribute("src", uri.AbsoluteUri);
                    assets.TryAdd(uri.AbsoluteUri, new Asset(uri.AbsoluteUri, kind));
                }
                catch (WenCunException)
                {
                    warnings.Add("已移除不受信任的资源地址。");
                }
            }
            if (string.IsNullOrWhiteSpace(style)) continue;
            var safe = style.Split(';').Select(part => part.Split(':', 2))
                .Where(pair => pair.Length == 2 && AllowedStyles.Contains(pair[0].Trim()) && SafeCssValue().IsMatch(pair[1].Trim()))
                .Where(pair => !pair[1].Contains("expression", StringComparison.OrdinalIgnoreCase) &&
                               !pair[1].Contains("url", StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair[0].Trim() + ":" + pair[1].Trim())
                .ToArray();
            if (safe.Length > 0) element.SetAttribute("style", string.Join(';', safe));
        }
        return assets.Values.ToArray();
    }

    public static string Localize(string html, IReadOnlyDictionary<string, string> map)
    {
        using var document = new HtmlParser().ParseDocument("<div id='wencun-root'>" + html + "</div>");
        var root = document.QuerySelector("#wencun-root")!;
        foreach (var element in root.QuerySelectorAll("img,audio,source"))
        {
            var source = element.GetAttribute("src");
            if (source is not null && map.TryGetValue(source, out var local))
            {
                element.SetAttribute("src", local.Replace(Path.DirectorySeparatorChar, '/'));
            }
            else if (source is not null && Uri.TryCreate(source, UriKind.Absolute, out var remote) && remote.Scheme == "https")
            {
                // 图片没有下载到本地时保留远程地址，导出文件仍可在线查看。
            }
            else
            {
                element.RemoveAttribute("src");
                if (element.LocalName == "img") element.SetAttribute("alt", "[图片未归档] " + element.GetAttribute("alt"));
                else element.TextContent = "[音频未归档，请查看原文]";
            }
        }
        return root.InnerHtml;
    }

    public static string StripImages(string html)
    {
        using var document = new HtmlParser().ParseDocument("<div id='wencun-root'>" + html + "</div>");
        var root = document.QuerySelector("#wencun-root")!;
        foreach (var image in root.QuerySelectorAll("img").ToArray()) image.Remove();
        return root.InnerHtml;
    }

    [GeneratedRegex(@"^[a-zA-Z0-9#(),.%\s+\-]{1,150}$")]
    private static partial Regex SafeCssValue();
}
