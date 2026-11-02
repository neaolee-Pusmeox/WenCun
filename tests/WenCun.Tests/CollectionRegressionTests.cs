using System.Net;
using System.Text.Json;
using WenCun.Core.Collect;
using WenCun.Core.Content;
using WenCun.Core.Domain;
using WenCun.Core.Net;
using WenCun.Core.Pending;
using WenCun.Core.Storage;

namespace WenCun.Tests;

public sealed class CollectionRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WenCunCollectionTests", Guid.NewGuid().ToString("N"));
    private const string Url = "https://mp.weixin.qq.com/s?__biz=MzA1&mid=100&idx=1";
    private static Session Session => new() { Biz = "MzA1", Cookie = "test=fixture", UserAgent = "fixture", SampleUrl = "https://mp.weixin.qq.com/mp/profile_ext?__biz=MzA1" };

    internal static string Page(int count = 1, bool more = false) => JsonSerializer.Serialize(new
    {
        ret = 0, can_msg_continue = more ? 1 : 0, next_offset = 10,
        general_msg_list = JsonSerializer.Serialize(new { list = Enumerable.Range(0, count).Select(i => new
        {
            comm_msg_info = new { datetime = 1700000000, type = 49 },
            app_msg_ext_info = new { title = "测试文章", content_url = Url.Replace("mid=100", "mid=" + (100 + i)) }
        }) })
    });

    [Fact]
    public async Task DiscoveryFromProfileRequestResolvesNameBeforeSavingBody()
    {
        var store = new PendingStore(_root);
        var library = new Library(Path.Combine(_root, "library"));
        using var client = new WeChatClient(new HttpClient(new Handler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.AbsolutePath == "/mp/profile_ext"
                ? Page() : "<a id='js_name'>正在嗅探的公众号</a><div id='js_content'>正文</div>")
        })));
        var collector = new Collector(library, client, store);
        await collector.DiscoverAsync(Session, "MzA1", "", null, CancellationToken.None);
        var pending = Assert.Single(store.LoadAll());
        Assert.Equal("正在嗅探的公众号", pending.Account);
        Assert.Equal(pending.Account, Assert.Single(pending.Articles).Account);
        Assert.Empty(library.Query(new LibraryQuery()).Items);
    }

    [Fact]
    public void ResolvingNameUpdatesExistingArticleRows()
    {
        var store = new PendingStore(_root);
        store.Merge("MzA1", "", [new PendingArticle("id", "MzA1", "", "标题", "", Url, null, DateTimeOffset.UtcNow)], 10, false);
        store.SetAccountName("MzA1", "正确公众号");
        Assert.Equal("正确公众号", Assert.Single(store.Load("MzA1")!.Articles).Account);
    }

    [Fact]
    public async Task CancellationDuringResourcesDoesNotCreateSavedArticleWithoutBody()
    {
        var library = new Library(Path.Combine(_root, "library"));
        using var cancelled = new CancellationTokenSource();
        using var client = new WeChatClient(new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.Host == "mmbiz.qpic.cn")
            {
                cancelled.Cancel();
                throw new TaskCanceledException();
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<div id='js_content'>正文<img src='https://mmbiz.qpic.cn/a.jpg'></div>") };
        })));
        var article = new Article { Id = UrlRules.ArticleId(Url), Url = Url, Biz = "MzA1" };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new Collector(library, client, new PendingStore(_root))
            .FetchBodyAsync(article, Session, cancelled.Token));
        Assert.Null(library.GetArticle(article.Id));
    }

    [Fact]
    public async Task DiscoveryDoesNotQueueBodyAlreadySavedInCurrentLibrary()
    {
        var library = new Library(Path.Combine(_root, "library"));
        var article = new Article { Id = UrlRules.ArticleId(Url), Url = Url, Biz = "MzA1", State = ContentState.Ready };
        library.UpsertArticles([article]);
        library.SaveContent(new ArticleContent(article.Id, "<p>已保存</p>", DateTimeOffset.UtcNow, [], []), ContentState.Ready);
        using var client = new WeChatClient(new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Page()) })));
        var pending = new PendingStore(_root);
        await new Collector(library, client, pending).DiscoverAsync(Session, "MzA1", "测试号", null, CancellationToken.None);
        Assert.Empty(pending.Load("MzA1")!.Articles);
        Assert.NotNull(library.GetContent(article.Id));
    }

    [Fact]
    public void FullLastPageHonorsExplicitTerminalFlag()
    {
        var page = WeChatClient.ParsePage(Page(10), 0, "测试号", "MzA1");
        Assert.Equal(10, page.Articles.Count);
        Assert.False(page.HasMore);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"ret\":0,\"general_msg_list\":\"invalid\"}")]
    [InlineData("{\"ret\":0,\"general_msg_list\":\"null\"}")]
    public void InvalidFeedShapesHaveProtocolFailure(string json)
    {
        var error = Assert.Throws<WenCunException>(() => WeChatClient.ParsePage(json, 0, "", "MzA1"));
        Assert.Equal(FailureKind.Protocol, error.Kind);
    }

    [Fact]
    public void RejectsForeignAccountInHistory()
    {
        var error = Assert.Throws<WenCunException>(() => WeChatClient.ParsePage(Page(), 0, "", "other"));
        Assert.Equal(FailureKind.Protocol, error.Kind);
    }

    [Fact]
    public void EmptyMessageIdentityIsRejected() =>
        Assert.Throws<WenCunException>(() => UrlRules.CanonicalArticleUrl(Url.Replace("mid=100", "mid=")));

    [Fact]
    public async Task ResourceTransportFailureDoesNotAbortArticleSave()
    {
        var library = new Library(Path.Combine(_root, "library"));
        using var client = new WeChatClient(new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.Host == "mmbiz.qpic.cn") throw new HttpRequestException("fixture disconnect");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<div id='js_content'>正文<img src='https://mmbiz.qpic.cn/a.jpg'></div>") };
        })));
        var article = new Article { Id = UrlRules.ArticleId(Url), Url = Url, Biz = "MzA1" };
        await new Collector(library, client, new PendingStore(_root), new SaveOptions([], ImageMode.Download))
            .FetchBodyAsync(article, Session, CancellationToken.None);
        var asset = Assert.Single(library.GetContent(article.Id)!.Assets);
        Assert.NotNull(asset.Error);
    }

    internal sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch (IOException) { /* SQLite 连接池在测试进程退出时释放。 */ }
    }
}
