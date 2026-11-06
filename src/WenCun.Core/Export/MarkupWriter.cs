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
        :root{color-scheme:light;--paper:#f1ece2;--sheet:#fbf8f1;--ink:#202723;--heading:#17201b;--muted:#6e7268;--faint:#8d9086;--rule:#ddd5c6;--accent:#2b5a4b;--seal:#b95037;--note:#f7eedf}
        *{box-sizing:border-box}
        html{background:var(--paper);scrollbar-color:#c9c2b4 var(--paper);scrollbar-width:thin}
        ::-webkit-scrollbar{width:10px;height:10px}
        ::-webkit-scrollbar-track{background:var(--paper)}
        ::-webkit-scrollbar-thumb{background:#c9c2b4;border:3px solid var(--paper);border-radius:6px}
        ::-webkit-scrollbar-thumb:hover{background:#a89f90}
        body{margin:0;padding:40px 24px 56px;background:var(--paper);color:var(--ink);font:16.5px/1.92 "HarmonyOS Sans SC","Noto Sans SC","Microsoft YaHei UI",system-ui,sans-serif;overflow-wrap:anywhere}
        .sheet{max-width:760px;margin:0 auto;background:var(--sheet);border:1px solid var(--rule);border-radius:10px;padding:58px 64px 52px}
        .brand{display:flex;align-items:center;gap:8px;color:var(--muted);font-size:11px;line-height:1;margin-bottom:36px}
        .brand svg{display:block;flex:none}
        .brand b{color:var(--heading);font-weight:600;font-size:12px;letter-spacing:.02em}
        .brand i{width:1px;height:11px;background:var(--rule);margin:0 2px}
        h1{font-family:"Noto Serif SC","Source Han Serif SC",STZhongsong,SimSun,serif;font-size:34px;line-height:1.42;font-weight:600;color:var(--heading);margin:0 0 16px}
        header{border-bottom:1px solid var(--rule);padding-bottom:26px;margin-bottom:32px}
        .meta{color:var(--muted);font-size:13px;line-height:1.9}
        .source{margin-top:4px;font-size:12.5px}
        a{color:var(--accent);text-decoration:underline;text-decoration-color:#b9cabd;text-underline-offset:3px}
        a:hover{text-decoration-color:var(--accent)}
        img{max-width:100%!important;height:auto!important;border-radius:4px}
        figure{margin:26px 0}
        figcaption{color:var(--faint);font-size:12px;line-height:1.7;margin-top:7px;text-align:center}
        p{margin:0 0 1.15em}
        main{font-size:16.5px}
        main h2,main h3,main h4{font-family:"Noto Serif SC","Source Han Serif SC",STZhongsong,SimSun,serif;color:var(--heading);line-height:1.5;margin:1.9em 0 .7em}
        main h2{font-size:25px}main h3{font-size:20px}main h4{font-size:17px}
        pre{white-space:pre-wrap;background:#f2ecdf;border:1px solid var(--rule);padding:16px 18px;border-radius:6px;font:13px/1.75 "Cascadia Mono",Consolas,monospace;overflow:auto}
        code{font-family:"Cascadia Mono",Consolas,monospace;font-size:.9em;background:#f2ecdf;border-radius:3px;padding:.1em .35em}
        pre code{background:none;padding:0}
        blockquote{border-left:2px solid var(--seal);margin:24px 0;padding:2px 0 2px 18px;color:#5c655c}
        blockquote p:last-child{margin-bottom:0}
        table{border-collapse:collapse;max-width:100%;font-size:14px;margin:24px 0}
        th,td{border:1px solid var(--rule);padding:9px 11px;text-align:left;vertical-align:top}
        th{background:#f3ede1;color:var(--heading);font-weight:600}
        aside{background:var(--note);border:1px solid #e5d5b7;border-left:2px solid var(--seal);padding:12px 15px;font-size:12.5px;line-height:1.8;margin:0 0 24px;border-radius:0 5px 5px 0;color:#6d5a3d}
        aside strong{color:#5f4b2e}
        audio{width:100%}
        footer{margin-top:44px;padding-top:18px;border-top:1px solid var(--rule);display:flex;align-items:center;gap:8px;color:var(--faint);font-size:11px;line-height:1.7}
        ::selection{background:#e3ebe3}
        @media (max-width:720px){body{padding:0}.sheet{border:0;border-radius:0;padding:34px 22px 40px}h1{font-size:28px}main{font-size:16px}}
        @media print{html,body{background:#fff}.sheet{max-width:none;border:0;border-radius:0;padding:0}body{padding:0;font-size:10.5pt;line-height:1.75}h1{font-size:24pt}.brand,footer{display:none}aside{break-inside:avoid}a{color:inherit;text-decoration:none}pre,img,blockquote,table{break-inside:avoid}}
        """;

    public static string Html(Article article, ArticleContent content, IReadOnlyDictionary<string, string> assets)
    {
        static string Encode(string value) => WebUtility.HtmlEncode(value);
        var body = HtmlSanitizer.Localize(content.BodyHtml, assets);
        var warnings = content.Warnings.Count == 0
            ? ""
            : "<aside><strong>归档提示</strong><br>" + Encode(string.Join("；", content.Warnings)) + "</aside>";
        var meta = string.Join(" · ", new[] { article.Account, article.Author, article.PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        const string mark = """
            <svg width="20" height="20" viewBox="0 0 24 24" aria-hidden="true" focusable="false">
              <path d="M3 8.3C3 7.6 3.6 7 4.3 7H19.7C20.4 7 21 7.6 21 8.3V9.8H3Z" fill="#173b32" opacity=".56"/>
              <path d="M8.3 4.5L15.4 3.9C16.1 3.8 16.8 4.3 16.9 5L17.6 10.9H7.9L7.5 5.6C7.4 5 7.7 4.6 8.3 4.5Z" fill="#f5efe2" stroke="#d8d0bf" stroke-width=".35"/>
              <path d="M9.8 6.8L14.5 6.3M10 8.5L13.3 8.2" stroke="#315441" stroke-width="1.05" stroke-linecap="round"/>
              <path d="M15.2 5L16.9 5.4C17.3 5.5 17.6 5.9 17.5 6.4L17.2 8.8C17.1 9.2 16.7 9.5 16.3 9.4L14.6 9C14.2 8.9 13.9 8.5 14 8L14.3 5.6C14.4 5.2 14.8 4.9 15.2 5Z" fill="#c65a3d"/>
              <path d="M3 10.1H21V17.2C21 19.1 19.5 20.6 17.6 20.6H6.4C4.5 20.6 3 19.1 3 17.2Z" fill="#173b32"/>
            </svg>
            """;
        return $$"""
            <!doctype html>
            <html lang="zh-CN"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="color-scheme" content="light">
            <title>{{Encode(article.Title)}}</title>
            <style>{{Css}}</style></head>
            <body><article class="sheet"><header>
            <div class="brand">{{mark}}<b>文存</b><i></i><span>本地归档</span></div>
            <h1>{{Encode(article.Title)}}</h1><div class="meta">{{Encode(meta)}}</div>
            <div class="source"><a href="{{Encode(article.Url)}}">查看原文</a></div></header>
            {{warnings}}<main>{{body}}</main>
            <footer>{{mark}}<span>文存 · 本地归档 · {{content.FetchedAt.ToLocalTime():yyyy-MM-dd HH:mm}}</span></footer>
            </article></body></html>
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
