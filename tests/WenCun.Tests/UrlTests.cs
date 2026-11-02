using WenCun.Core.Content;
using WenCun.Core.Domain;

namespace WenCun.Tests;

public sealed class UrlTests
{
    [Fact]
    public void CanonicalizesTrackingParameters()
    {
        var url = UrlRules.CanonicalArticleUrl(
            "https://mp.weixin.qq.com/s?__biz=MzA1&mid=100&idx=1&sn=abc&chksm=zzz&scene=21#wechat_redirect");
        Assert.Equal("https://mp.weixin.qq.com/s?__biz=MzA1&mid=100&idx=1&sn=abc", url);
    }

    [Fact]
    public void ArticleIdIgnoresSignature()
    {
        var first = UrlRules.ArticleId("https://mp.weixin.qq.com/s?__biz=MzA1&mid=100&idx=1&sn=abc");
        var second = UrlRules.ArticleId("https://mp.weixin.qq.com/s?__biz=MzA1&mid=100&idx=1&sn=xyz");
        Assert.Equal(first, second);
    }

    [Fact]
    public void RejectsForeignHost()
    {
        Assert.Throws<WenCunException>(() => UrlRules.CanonicalArticleUrl("https://example.com/s?__biz=1&mid=1&idx=1"));
    }

    [Fact]
    public void SanitizesFileNames()
    {
        Assert.Equal("a b", UrlRules.SafeFileName("a:b"));
        Assert.Equal("未命名", UrlRules.SafeFileName("   "));
    }
}
