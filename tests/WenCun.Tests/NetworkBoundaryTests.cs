using System.Net;
using WenCun.Core.Domain;
using WenCun.Core.Net;

namespace WenCun.Tests;

public sealed class NetworkBoundaryTests
{
    private static Session Session => new() { Biz = "biz", Cookie = "secret=fixture", UserAgent = "fixture" };

    [Fact]
    public async Task FollowsValidatedSameHostRedirect()
    {
        var calls = new List<string>();
        using var client = new WeChatClient(new HttpClient(new CollectionRegressionTests.Handler(request =>
        {
            calls.Add(request.RequestUri!.AbsolutePath);
            if (calls.Count == 1)
                return new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("/s/next", UriKind.Relative) } };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("正文") };
        })));
        Assert.Equal("正文", await client.FetchArticleAsync(Session, "https://mp.weixin.qq.com/s/start", CancellationToken.None));
        Assert.Equal(new[] { "/s/start", "/s/next" }, calls);
    }

    [Fact]
    public async Task RejectsForeignRedirectBeforeSendingCredentials()
    {
        var calls = 0;
        using var client = new WeChatClient(new HttpClient(new CollectionRegressionTests.Handler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://example.com/collect") } };
        })));
        var error = await Assert.ThrowsAsync<WenCunException>(() => client.FetchArticleAsync(Session, "https://mp.weixin.qq.com/s/start", CancellationToken.None));
        Assert.Equal(FailureKind.Input, error.Kind);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DownloadTimeoutIsNetworkFailure()
    {
        using var client = new WeChatClient(new HttpClient(new CollectionRegressionTests.Handler(_ => throw new TaskCanceledException())));
        var error = await Assert.ThrowsAsync<WenCunException>(() => client.DownloadAsync("https://mmbiz.qpic.cn/a.jpg", CancellationToken.None));
        Assert.Equal(FailureKind.Network, error.Kind);
    }
}
