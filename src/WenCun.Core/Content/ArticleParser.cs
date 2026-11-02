using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using WenCun.Core.Domain;

namespace WenCun.Core.Content;

public sealed record ParsedArticle(Article Article, ArticleContent Content);

public sealed partial class ArticleParser
{
    public static string ReadAccountName(string html)
    {
        using var document = new HtmlParser().ParseDocument(html);
        return AccountName(document);
    }

    private static string AccountName(IDocument document) =>
        document.QuerySelectorAll("#js_name, .profile_nickname, .rich_media_meta_nickname, .account_nickname_inner")
            .Select(element => element.TextContent.Trim())
            .FirstOrDefault(name => name.Length > 0) ?? "";

    public ParsedArticle Parse(string html, Article metadata)
    {
        if (html.Length > 12 * 1024 * 1024)
            throw new WenCunException(FailureKind.Unavailable, "文章页面过大。");
        using var document = new HtmlParser().ParseDocument(html);
        var content = document.QuerySelector("#js_content, .rich_media_content");
        var pageText = document.Body?.TextContent ?? "";
        if (content is null || string.IsNullOrWhiteSpace(content.TextContent) && content.QuerySelector("img,audio") is null)
        {
            if (pageText.Contains("访问过于频繁", StringComparison.Ordinal) || pageText.Contains("环境异常", StringComparison.Ordinal))
                throw new WenCunException(FailureKind.RateLimited, "微信返回了访问限制页面，请稍后再试。");
            if (pageText.Contains("登录", StringComparison.Ordinal) || pageText.Contains("微信客户端打开", StringComparison.Ordinal))
                throw new WenCunException(FailureKind.Session, "当前会话没有取得正文，请重新采集。");
            throw new WenCunException(FailureKind.Unavailable, "页面没有可用正文，可能已删除或页面结构发生变化。");
        }

        static string Text(IElement? element) => WebUtility.HtmlDecode(element?.TextContent ?? "").Trim();
        var title = Text(document.QuerySelector("#activity-name"));
        if (title.Length == 0)
            title = document.QuerySelector("meta[property='og:title']")?.GetAttribute("content") ?? metadata.Title;
        var account = AccountName(document);
        var author = Text(document.QuerySelector("#js_author_name"));
        if (author.Length == 0)
        {
            author = Text(document.QuerySelector(".rich_media_meta_text"));
            if (author == account) author = "";
        }
        var published = ParsePublished(Text(document.QuerySelector("#publish_time"))) ?? metadata.PublishedAt;
        if (published is null)
        {
            var match = Timestamp().Match(html);
            if (match.Success && long.TryParse(match.Groups[1].Value, out var seconds) && seconds is > 0 and < 253402300800)
                published = DateTimeOffset.FromUnixTimeSeconds(seconds);
        }

        var paywall = document.QuerySelector("#js_paywall, #js_pay_content, .paywall") is not null ||
                      pageText.Contains("付费后可阅读全文", StringComparison.Ordinal) ||
                      pageText.Contains("购买后可阅读全文", StringComparison.Ordinal);
        var warnings = new List<string>();
        if (paywall) warnings.Add("付费或受限文章：只归档当前会话返回的内容。");
        if (content.QuerySelector("iframe,video,mp-video") is not null) warnings.Add("文内视频未归档。");

        foreach (var voice in content.QuerySelectorAll("mpvoice,mp-common-mpaudio"))
        {
            var source = voice.GetAttribute("src") ?? voice.GetAttribute("data-src");
            if (voice.GetAttribute("voice_encode_fileid") is { Length: > 0 and <= 512 } voiceId)
                source = "https://res.wx.qq.com/voice/getvoice?mediaid=" + Uri.EscapeDataString(voiceId);
            var audio = document.CreateElement("audio");
            if (source is not null) audio.SetAttribute("src", source);
            else warnings.Add("一段微信语音没有可下载地址。");
            audio.SetAttribute("controls", "controls");
            audio.TextContent = "语音内容";
            voice.Replace(audio);
        }

        var assets = HtmlSanitizer.Clean(content, warnings);
        if (string.IsNullOrWhiteSpace(content.TextContent) && content.QuerySelector("img,audio") is null)
            throw new WenCunException(FailureKind.Unavailable, "净化后没有可归档的正文。");

        var article = ResolveIdentity(document, metadata) with
        {
            Title = title.Trim(),
            Account = account.Length > 0 ? account : metadata.Account,
            Author = author,
            PublishedAt = published,
            State = paywall ? ContentState.Partial : ContentState.Ready,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var body = content.InnerHtml;
        return new ParsedArticle(article, new ArticleContent(article.Id, body, DateTimeOffset.UtcNow, assets, warnings.Distinct().ToArray()));
    }

    private static DateTimeOffset? ParsePublished(string text)
    {
        if (DateTimeOffset.TryParseExact(text, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var exact))
            return exact;
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var loose))
            return loose;
        return null;
    }

    private static Article ResolveIdentity(IDocument document, Article article)
    {
        var original = new Uri(article.Url);
        Article? resolved = null;
        void Check(Article candidate)
        {
            if (article.Biz.Length > 0 && candidate.Biz != article.Biz)
                throw new WenCunException(FailureKind.Protocol, "文章页面给出的公众号与请求不一致，正文未入库。");
            if (original.AbsolutePath == "/s" && candidate.Id != article.Id)
                throw new WenCunException(FailureKind.Protocol, "文章页面给出的文章标识与请求不一致，正文未入库。");
        }
        Article? FromUrl(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return null;
            string canonical;
            try { canonical = UrlRules.CanonicalArticleUrl(candidate); }
            catch (WenCunException) { return null; }
            var query = UrlRules.Query(new Uri(canonical));
            if (!query.ContainsKey("__biz") || !query.ContainsKey("mid") || !query.ContainsKey("idx")) return null;
            var identified = article with
            {
                Id = UrlRules.ArticleId(canonical), Url = canonical, Biz = query["__biz"],
                Index = int.TryParse(query["idx"], CultureInfo.InvariantCulture, out var index) ? index : article.Index
            };
            Check(identified);
            return identified;
        }

        foreach (var tag in document.QuerySelectorAll("meta[property='og:url'],link[rel='canonical']"))
            if (FromUrl(tag.GetAttribute("content") ?? tag.GetAttribute("href")) is { } identified) resolved = identified;

        var script = string.Join('\n', document.QuerySelectorAll("script").Select(s => s.TextContent));
        var values = Variables().Matches(script).Cast<Match>()
            .GroupBy(m => m.Groups[1].Value)
            .ToDictionary(g => g.Key, g => g.First().Groups[2].Value, StringComparer.Ordinal);
        var biz = values.GetValueOrDefault("biz") ?? values.GetValueOrDefault("__biz");
        var mid = values.GetValueOrDefault("mid") ?? values.GetValueOrDefault("appmsgid");
        var idx = values.GetValueOrDefault("idx");
        if (biz is not null && article.Biz.Length > 0 && biz != article.Biz)
            throw new WenCunException(FailureKind.Protocol, "文章页面给出的公众号与请求不一致，正文未入库。");
        if (biz is not null && mid is not null && idx is not null)
        {
            var url = $"https://mp.weixin.qq.com/s?__biz={Uri.EscapeDataString(biz)}&mid={Uri.EscapeDataString(mid)}&idx={Uri.EscapeDataString(idx)}";
            if (values.GetValueOrDefault("sn") is { Length: > 0 } sn) url += "&sn=" + Uri.EscapeDataString(sn);
            if (FromUrl(url) is { } identified) resolved = identified;
        }
        return resolved ?? (biz is not null && article.Biz.Length == 0 ? article with { Biz = biz } : article);
    }

    [GeneratedRegex("(?:create_time|ct)\\s*[=:]\\s*['\"]?(\\d{10})")]
    private static partial Regex Timestamp();

    [GeneratedRegex("\\b(?:var|let|const)\\s+(biz|__biz|mid|appmsgid|idx|sn)\\s*=\\s*['\"]([^'\"]+)['\"]")]
    private static partial Regex Variables();
}
