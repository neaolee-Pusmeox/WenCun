using WenCun.Core.Domain;
using WenCun.Core.Net;

namespace WenCun.Tests;

public sealed class FeedTests
{
    private const string Page = """
        {
          "ret": 0,
          "general_msg_list": "{\"list\":[{\"comm_msg_info\":{\"type\":49,\"datetime\":1700000000},\"app_msg_ext_info\":{\"title\":\"第一篇\",\"content_url\":\"https:\\/\\/mp.weixin.qq.com\\/s?__biz=MzA1&mid=100&idx=1&sn=abc\",\"author\":\"作者\",\"is_multi\":1,\"multi_app_msg_item_list\":[{\"title\":\"第二篇\",\"content_url\":\"https:\\/\\/mp.weixin.qq.com\\/s?__biz=MzA1&mid=100&idx=2&sn=def\"}]}},{\"comm_msg_info\":{\"type\":1,\"datetime\":1700000001}}]}",
          "next_offset": 10,
          "can_msg_continue": 1,
          "msg_count": 2
        }
        """;

    [Fact]
    public void ParsesMultiArticlePage()
    {
        var page = WeChatClient.ParsePage(Page, 0, "测试号", "MzA1");
        Assert.Equal(2, page.Articles.Count);
        Assert.Equal(10, page.NextOffset);
        Assert.True(page.HasMore);
        Assert.Equal("第一篇", page.Articles[0].Title);
        Assert.Equal("第二篇", page.Articles[1].Title);
        Assert.Equal(2, page.Articles[1].Index);
        Assert.All(page.Articles, article => Assert.Equal("MzA1", article.Biz));
        Assert.All(page.Articles, article => Assert.Equal(ContentState.NotFetched, article.State));
    }

    [Fact]
    public void RejectsRateLimit()
    {
        var error = Assert.Throws<WenCunException>(() => WeChatClient.ParsePage("{\"ret\":-6}", 0, "", "MzA1"));
        Assert.Equal(FailureKind.RateLimited, error.Kind);
    }

    [Fact]
    public void RejectsMissingList()
    {
        var error = Assert.Throws<WenCunException>(() =>
            WeChatClient.ParsePage("{\"ret\":0,\"msg_count\":0,\"can_msg_continue\":0}", 0, "", "MzA1"));
        Assert.Equal(FailureKind.Protocol, error.Kind);
    }

    [Fact]
    public void RejectsStalledCursor()
    {
        var json = "{\"ret\":0,\"general_msg_list\":\"{\\\"list\\\":[]}\",\"next_offset\":0,\"can_msg_continue\":1}";
        var error = Assert.Throws<WenCunException>(() => WeChatClient.ParsePage(json, 0, "", "MzA1"));
        Assert.Equal(FailureKind.Protocol, error.Kind);
    }
}
