using WenCun.Core.Content;

namespace WenCun.Tests;

public sealed class SanitizerTests
{
    [Fact]
    public void StripImagesRemovesOnlyImages()
    {
        var html = HtmlSanitizer.StripImages("<p>正文</p><img src=\"https://mmbiz.qpic.cn/a.jpg\" alt=\"图\"><audio src=\"https://res.wx.qq.com/a.mp3\"></audio>");
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("正文", html);
        Assert.Contains("audio", html, StringComparison.OrdinalIgnoreCase);
    }
}
