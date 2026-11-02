using System.Net;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using WenCun.Core.Content;
using WenCun.Core.Domain;

namespace WenCun.Core.Export;

public static class MarkupWriter
{
    private const string Css = """
        :root{color-scheme:light}
        body{font:17px/1.9 "Microsoft YaHei UI",system-ui,sans-serif;color:#1f2733;background:#fff;max-width:760px;margin:48px auto;padding:0 28px;overflow-wrap:anywhere}
        h1{font-size:30px;line-height:1.45;margin:0 0 12px}
        header{border-bottom:1px solid #e6e8ec;padding-bottom:22px;margin-bottom:28px}
        .meta{color:#6b7686;font-size:14px}
        a{color:#2f6feb}
        img{max-width:100%!important;height:auto!important}
        pre{white-space:pre-wrap;background:#f5f6f8;padding:16px;border-radius:8px}
        blockquote{border-left:3px solid #b9c6e6;margin-left:0;padding-left:16px;color:#5b6675}
        table{border-collapse:collapse;max-width:100%}
        td,th{border:1px solid #dfe3ea;padding:8px}
        aside{background:#fff7e2;padding:12px 14px;font-size:14px;margin-bottom:22px;border-radius:6px}
        footer{margin-top:40px;font-size:12px;color:#8a94a3}
        @media print{body{margin:0;max-width:none}a{color:inherit}pre,img,blockquote{break-inside:avoid}}
        """;

    public static string Html(Article article, ArticleContent content, IReadOnlyDictionary<string, string> assets)
    {
        static string Encode(string value) => WebUtility.HtmlEncode(value);
        var body = HtmlSanitizer.Localize(content.BodyHtml, assets);
        var warnings = content.Warnings.Count == 0
            ? ""
            : "<aside>" + Encode(string.Join(" / ", content.Warnings)) + "</aside>";
        var meta = string.Join(" · ", new[] { article.Account, article.Author, article.PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        return $$"""
            <!doctype html>
            <html lang="zh-CN"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{Encode(article.Title)}}</title>
            <style>{{Css}}</style></head>
            <body><header><h1>{{Encode(article.Title)}}</h1><div class="meta">{{Encode(meta)}}</div>
            <div class="meta"><a href="{{Encode(article.Url)}}">查看原文</a></div></header>
            {{warnings}}<main>{{body}}</main>
            <footer>文存 · 本地归档 · {{content.FetchedAt.ToLocalTime():yyyy-MM-dd HH:mm}}</footer></body></html>
            """;
    }

    public static string Markdown(Article article, ArticleContent content, IReadOnlyDictionary<string, string> assets)
    {
        using var document = new HtmlParser().ParseDocument("<div id='root'>" + content.BodyHtml + "</div>");
        var builder = new StringBuilder();
        builder.Append("# ").AppendLine(article.Title).AppendLine();
        builder.Append("- 公众号：").AppendLine(article.Account);
        if (article.Author.Length > 0) builder.Append("- 作者：").AppendLine(article.Author);
        if (article.PublishedAt is { } published) builder.Append("- 发布：").AppendLine(published.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        builder.Append("- 原文：").AppendLine(article.Url).AppendLine();
        foreach (var warning in content.Warnings) builder.Append("> 注意：").AppendLine(warning).AppendLine();
        builder.Append(MarkdownNode(document.QuerySelector("#root")!, assets));
        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string MarkdownNode(INode node, IReadOnlyDictionary<string, string> assets)
    {
        if (node is IText text) return text.Data;
        if (node is not IElement element) return "";
        var inner = string.Concat(element.ChildNodes.Select(child => MarkdownNode(child, assets)));
        return element.LocalName switch
        {
            "h1" or "h2" or "h3" or "h4" or "h5" or "h6" =>
                "\n\n" + new string('#', element.LocalName[1] - '0') + " " + Inline(element, assets).Trim() + "\n\n",
            "p" or "div" or "section" or "article" or "figure" or "figcaption" => "\n\n" + inner.Trim() + "\n\n",
            "br" => "  \n",
            "strong" or "b" => "**" + inner + "**",
            "em" or "i" => "*" + inner + "*",
            "del" or "s" => "~~" + inner + "~~",
            "code" => element.ParentElement?.LocalName == "pre" ? inner : "`" + inner + "`",
            "pre" => "\n\n```\n" + element.TextContent.TrimEnd() + "\n```\n\n",
            "blockquote" => "\n\n" + Prefix(inner.Trim(), "> ") + "\n\n",
            "ul" or "ol" => "\n" + string.Concat(element.Children.Where(child => child.LocalName == "li")
                .Select((child, index) => (element.LocalName == "ol" ? $"{index + 1}. " : "- ") + Inline(child, assets).Trim() + "\n")) + "\n",
            "li" => inner,
            "hr" => "\n\n---\n\n",
            "a" => element.GetAttribute("href") is { Length: > 0 } href ? $"[{inner.Trim()}]({href})" : inner,
            "img" => ImageMarkdown(element, assets),
            "audio" => AudioMarkdown(element, assets),
            "table" => "\n\n" + HtmlSanitizer.Localize(element.OuterHtml, assets) + "\n\n",
            _ => inner
        };
    }

    private static string Inline(IElement element, IReadOnlyDictionary<string, string> assets) =>
        string.Concat(element.ChildNodes.Select(child => MarkdownNode(child, assets)));

    private static string ImageMarkdown(IElement element, IReadOnlyDictionary<string, string> assets)
    {
        var source = element.GetAttribute("src");
        var alt = element.GetAttribute("alt") ?? "图片";
        if (source is not null && assets.TryGetValue(source, out var local))
            return $"![{alt}]({local.Replace('\\', '/')})";
        return source is not null && Uri.TryCreate(source, UriKind.Absolute, out _)
            ? $"![{alt}]({source})"
            : $"[{alt}未归档]";
    }

    private static string AudioMarkdown(IElement element, IReadOnlyDictionary<string, string> assets)
    {
        var source = element.GetAttribute("src");
        return source is not null && assets.TryGetValue(source, out var local)
            ? $"[音频]({local.Replace('\\', '/')})"
            : "[音频未归档]";
    }

    private static string Prefix(string text, string prefix) =>
        string.Join('\n', text.Split('\n').Select(line => prefix + line));

    public static string Text(Article article, ArticleContent content)
    {
        using var document = new HtmlParser().ParseDocument("<div id='root'>" + content.BodyHtml + "</div>");
        var builder = new StringBuilder()
            .AppendLine(article.Title)
            .AppendLine(string.Join(" · ", new[] { article.Account, article.Author, article.PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") }
                .Where(value => !string.IsNullOrWhiteSpace(value))))
            .AppendLine(article.Url)
            .AppendLine();
        foreach (var warning in content.Warnings) builder.AppendLine("注意：" + warning);
        Walk(document.QuerySelector("#root")!, builder);
        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static void Walk(INode node, StringBuilder builder)
    {
        if (node is IText text)
        {
            builder.Append(text.Data);
            return;
        }
        if (node is not IElement element) return;
        switch (element.LocalName)
        {
            case "img":
                builder.Append("[图片] ").AppendLine(element.GetAttribute("alt") ?? "");
                return;
            case "audio":
                builder.AppendLine("[音频]");
                return;
            case "br":
                builder.AppendLine();
                return;
            case "table":
                foreach (var row in element.QuerySelectorAll("tr"))
                {
                    var cells = row.Children.Where(cell => cell.LocalName is "td" or "th")
                        .Select(cell => cell.TextContent.Trim().Replace('\n', ' '));
                    builder.AppendLine(string.Join("\t", cells));
                }
                builder.AppendLine();
                return;
        }
        foreach (var child in element.ChildNodes) Walk(child, builder);
        if (element.LocalName is "p" or "div" or "section" or "li" or "blockquote" or "pre" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
            builder.AppendLine();
    }
}
