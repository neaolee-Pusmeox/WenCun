using WenCun.Core.Content;
using WenCun.Core.Domain;

namespace WenCun.Tests;

public sealed class ParserTests
{
    private const string Html = """
        <html><head><meta property="og:title" content="备用标题"></head><body>
        <h1 id="activity-name">测试文章</h1>
        <a id="js_name">测试公众号</a>
        <span id="js_author_name">作者甲</span>
        <em id="publish_time">2024-05-01 10:00</em>
        <div id="js_content">
          <p onclick="evil()">正文<strong>加粗</strong><script>alert(1)</script></p>
          <img data-src="https://mmbiz.qpic.cn/a.jpg" alt="图">
          <iframe src="https://evil.example"></iframe>
        </div>
        </body></html>
        """;

    private static Article Meta() => new()
    {
        Id = "id",
        Url = "https://mp.weixin.qq.com/s?__biz=MzA1&mid=100&idx=1",
        Biz = "MzA1"
    };

    [Fact]
    public void ParsesMetadataAndSanitizes()
    {
        var parsed = new ArticleParser().Parse(Html, Meta());
        Assert.Equal("测试文章", parsed.Article.Title);
        Assert.Equal("测试公众号", parsed.Article.Account);
        Assert.Equal("作者甲", parsed.Article.Author);
        Assert.Equal(ContentState.Ready, parsed.Article.State);
        Assert.NotNull(parsed.Article.PublishedAt);
        Assert.DoesNotContain("script", parsed.Content.BodyHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("iframe", parsed.Content.BodyHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", parsed.Content.BodyHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("https://mmbiz.qpic.cn/a.jpg", parsed.Content.BodyHtml);
        Assert.Single(parsed.Content.Assets);
        Assert.Equal("image", parsed.Content.Assets[0].Kind);
    }

    [Fact]
    public void MarksPaywallAsPartial()
    {
        var html = Html.Replace("<div id=\"js_content\">", "<div id=\"js_paywall\"></div><div id=\"js_content\">", StringComparison.Ordinal);
        var parsed = new ArticleParser().Parse(html, Meta());
        Assert.Equal(ContentState.Partial, parsed.Article.State);
        Assert.NotEmpty(parsed.Content.Warnings);
    }

    [Fact]
    public void RejectsPageWithoutBody()
    {
        var error = Assert.Throws<WenCunException>(() => new ArticleParser().Parse("<html><body>访问过于频繁</body></html>", Meta()));
        Assert.Equal(FailureKind.RateLimited, error.Kind);
    }
}
