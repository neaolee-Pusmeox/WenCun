using Microsoft.Data.Sqlite;
using WenCun.Core.Domain;
using WenCun.Core.Storage;

namespace WenCun.Tests;

public sealed class LibraryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "WenCunTests", Guid.NewGuid().ToString("N"));
    private readonly Library _library;

    public LibraryTests() => _library = new Library(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private static Article Article(string id, string title, string account = "测试号", ContentState state = ContentState.NotFetched) => new()
    {
        Id = id,
        Url = "https://mp.weixin.qq.com/s?__biz=MzA1&mid=" + id + "&idx=1",
        Biz = "MzA1",
        Account = account,
        Title = title,
        PublishedAt = DateTimeOffset.Parse("2024-05-01T10:00:00+08:00"),
        State = state
    };

    [Fact]
    public void UpsertsAndSearches()
    {
        var (added, updated) = _library.UpsertArticles([Article("1", "第一篇"), Article("2", "第二篇")]);
        Assert.Equal(2, added);
        Assert.Equal(0, updated);
        var again = _library.UpsertArticles([Article("1", "第一篇改名")]);
        Assert.Equal(0, again.Added);
        Assert.Equal(1, again.Updated);
        Assert.Equal("第一篇改名", _library.GetArticle("1")!.Title);

        var page = _library.Query(new LibraryQuery(Search: "改名"));
        Assert.Single(page.Items);
        Assert.Equal(2, _library.Query(new LibraryQuery()).Total);
    }

    [Fact]
    public void SavesContentAndListsAccounts()
    {
        _library.UpsertArticles([Article("1", "第一篇")]);
        _library.SaveContent(new ArticleContent("1", "<p>正文</p>", DateTimeOffset.UtcNow,
            [new Asset("https://mmbiz.qpic.cn/a.jpg", "image", "assets/1/a.jpg")], []), ContentState.Ready);
        var content = _library.GetContent("1");
        Assert.NotNull(content);
        Assert.Equal("<p>正文</p>", content.BodyHtml);
        Assert.Single(content.Assets);
        Assert.Equal(ContentState.Ready, _library.GetArticle("1")!.State);

        var accounts = _library.Accounts();
        Assert.Single(accounts);
        Assert.Equal(1, accounts[0].Count);
        Assert.Equal("测试号", accounts[0].Name);
    }

    [Fact]
    public void KeepsCheckpoint()
    {
        Assert.Equal((0, false), _library.GetCheckpoint("MzA1"));
        _library.SaveCheckpoint("MzA1", "测试号", 30, false);
        Assert.Equal((30, false), _library.GetCheckpoint("MzA1"));
        _library.SaveCheckpoint("MzA1", "测试号", 0, true);
        Assert.Equal((0, true), _library.GetCheckpoint("MzA1"));
    }

    [Fact]
    public void DeletesArticleAndContent()
    {
        _library.UpsertArticles([Article("1", "第一篇")]);
        _library.SaveContent(new ArticleContent("1", "<p>正文</p>", DateTimeOffset.UtcNow, [], []), ContentState.Ready);
        _library.DeleteArticles(["1"]);
        Assert.Null(_library.GetArticle("1"));
        Assert.Null(_library.GetContent("1"));
    }

    [Fact]
    public void TracksFailureAndExportState()
    {
        _library.UpsertArticles([Article("1", "第一篇")]);
        _library.SetFailure("1", "网络失败");
        var failed = _library.GetArticle("1")!;
        Assert.Equal(ContentState.Failed, failed.State);
        Assert.Equal("网络失败", failed.LastError);
        Assert.Single(_library.FailedArticles("MzA1"));

        _library.SaveContent(new ArticleContent("1", "<p>正文</p>", DateTimeOffset.UtcNow, [], []), ContentState.Ready);
        var saved = _library.GetArticle("1")!;
        Assert.Equal(ContentState.Ready, saved.State);
        Assert.Null(saved.LastError);

        _library.SetExportResult("1", ExportState.Failed, "PDF 失败");
        var exportFailed = _library.GetArticle("1")!;
        Assert.Equal(ExportState.Failed, exportFailed.ExportState);
        Assert.Equal("PDF 失败", exportFailed.ExportError);
        Assert.Single(_library.ExportFailedArticles("MzA1"));

        var account = _library.Accounts().Single();
        Assert.Equal(1, account.Saved);
        Assert.Equal(0, account.Failed);
        Assert.Equal(0, account.Exported);
        Assert.Equal(1, account.ExportFailed);
    }

    [Fact]
    public void CopiesLibraryToNewFolder()
    {
        _library.UpsertArticles([Article("1", "第一篇")]);
        _library.SaveContent(new ArticleContent("1", "<p>正文</p>", DateTimeOffset.UtcNow, [], []), ContentState.Ready);
        var target = Path.Combine(Path.GetTempPath(), "WenCunCopyTests", Guid.NewGuid().ToString("N"));
        try
        {
            _library.CopyTo(target);
            var copy = new Library(target);
            Assert.Single(copy.Query(new LibraryQuery()).Items);
            Assert.Equal("<p>正文</p>", copy.GetContent("1")!.BodyHtml);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(target, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void DeletesAccountDataAndExports()
    {
        _library.UpsertArticles([Article("1", "第一篇")]);
        _library.SaveContent(new ArticleContent("1", "<p>正文</p>", DateTimeOffset.UtcNow, [], []), ContentState.Ready);
        var exportFolder = Path.Combine(_directory, "articles", "测试号", "2024-05-01 第一篇");
        Directory.CreateDirectory(exportFolder);
        File.WriteAllText(Path.Combine(exportFolder, "article.html"), "x");

        var (articles, _, exports) = _library.DeleteAccountData("MzA1");
        Assert.Equal(1, articles);
        Assert.Equal(1, exports);
        Assert.Empty(_library.Query(new LibraryQuery()).Items);
        Assert.Empty(_library.Accounts());
        Assert.False(Directory.Exists(exportFolder));
    }
}
