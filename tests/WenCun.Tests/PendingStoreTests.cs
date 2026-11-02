using WenCun.Core.Domain;
using WenCun.Core.Pending;

namespace WenCun.Tests;

public sealed class PendingStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WenCunPendingTests", Guid.NewGuid().ToString("N"));
    private readonly PendingStore _store;

    public PendingStoreTests() => _store = new PendingStore(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static PendingArticle Article(string id, string title = "标题") => new(
        id, "MzA1", "测试号", title, "作者",
        "https://mp.weixin.qq.com/s?__biz=MzA1&mid=" + id + "&idx=1",
        DateTimeOffset.Parse("2024-05-01T10:00:00+08:00"), DateTimeOffset.UtcNow);

    [Fact]
    public void MergesAndDeduplicates()
    {
        var first = _store.Merge("MzA1", "测试号", [Article("1"), Article("2")], 10, false);
        Assert.Equal(2, first.Articles.Count);
        Assert.Equal(10, first.Offset);
        Assert.False(first.Complete);

        var second = _store.Merge("MzA1", "测试号", [Article("1", ""), Article("3")], 20, true);
        Assert.Equal(3, second.Articles.Count);
        Assert.Equal("标题", second.Articles.Single(article => article.Id == "1").Title);
        Assert.True(second.Complete);
        Assert.Single(_store.LoadAll());
    }

    [Fact]
    public void RemovesAndClears()
    {
        _store.Merge("MzA1", "测试号", [Article("1"), Article("2")], 10, false);
        _store.RemoveArticles("MzA1", ["1"]);
        Assert.Single(_store.Load("MzA1")!.Articles);
        _store.Clear("MzA1");
        Assert.Empty(_store.Load("MzA1")!.Articles);
    }

    [Fact]
    public void StoresError()
    {
        _store.Merge("MzA1", "测试号", [Article("1")], 10, false);
        _store.SetError("MzA1", "1", "网络失败");
        Assert.Equal("网络失败", _store.Load("MzA1")!.Articles.Single().LastError);
    }

    [Fact]
    public void RemovesAccount()
    {
        _store.Merge("MzA1", "测试号", [Article("1")], 10, false);
        _store.RemoveAccount("MzA1");
        Assert.Null(_store.Load("MzA1"));
        Assert.Empty(_store.LoadAll());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Biz\":\"MzA1\",\"Articles\":null}")]
    public void MalformedSchemaIsNotExposedAsPendingAccount(string json)
    {
        Directory.CreateDirectory(_store.Directory);
        File.WriteAllText(_store.PathFor("MzA1"), json);
        Assert.Empty(_store.LoadAll());
    }

    [Fact]
    public void MergeDoesNotOverwriteUnreadablePendingFile()
    {
        Directory.CreateDirectory(_store.Directory);
        var path = _store.PathFor("MzA1");
        File.WriteAllText(path, "broken json");
        Assert.Throws<WenCunException>(() => _store.Merge("MzA1", "测试号", [Article("1")], 10, true));
        Assert.Equal("broken json", File.ReadAllText(path));
    }
}
