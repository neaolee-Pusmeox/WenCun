using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Microsoft.Data.Sqlite;
using WenCun.Core.Domain;
using WenCun.Core.Export;
using WenCun.Core.Storage;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace WenCun.Tests;

public sealed class StorageExportRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WenCunStorageExportRegression", Guid.NewGuid().ToString("N"));
    private readonly Library _library;
    private string ExportRoot => Path.Combine(_library.RootDirectory, "articles");
    public StorageExportRegressionTests() => _library = new Library(Path.Combine(_root, "library"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }

    private static Article Article(string id = "one", string biz = "biz", string account = "公众号") => new()
    {
        Id = id, Biz = biz, Account = account, Title = "相同标题", Index = 3,
        Url = "https://mp.weixin.qq.com/s?__biz=" + biz + "&mid=1&idx=3",
        PublishedAt = DateTimeOffset.Parse("2024-05-01T10:00:00+08:00")
    };

    private Article Save(Article? article = null, string body = "<p>新正文</p>", params Asset[] assets)
    {
        article ??= Article();
        _library.UpsertArticles([article]);
        _library.SaveContent(new ArticleContent(article.Id, body, DateTimeOffset.UtcNow, assets, []), ContentState.Ready);
        return article;
    }

    private string Put(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private Task<ExportResult> Export(Article article, IPdfRenderer? renderer = null, params ExportFormat[] formats) =>
        new Exporter(_library, renderer).ExportAsync(new ExportRequest([article], ExportRoot, formats), null, CancellationToken.None);

    private sealed class Renderer(Func<string, string, CancellationToken, Task> render) : IPdfRenderer
    {
        public Task RenderPdfAsync(string htmlPath, string pdfPath, CancellationToken ct) => render(htmlPath, pdfPath, ct);
    }

    [Fact]
    public void ArticleIndexSurvivesInsertUpdateAndReopen()
    {
        var article = Save();
        Assert.Equal(3, new Library(_library.RootDirectory).GetArticle(article.Id)!.Index);
        _library.UpsertArticles([article with { Index = 4 }]);
        Assert.Equal(4, _library.Query(new LibraryQuery()).Items.Single().Index);
    }

    [Fact]
    public void LargePageDoesNotOverflowBackToFirstPage()
    {
        Save();
        Assert.Empty(_library.Query(new LibraryQuery(Page: int.MaxValue, PageSize: 200)).Items);
    }

    [Fact]
    public void CopyUsesDatabaseSnapshotWithoutCopyingLiveWalFiles()
    {
        var article = Save();
        var target = Path.Combine(_root, "copy");
        Assert.True(File.Exists(_library.DatabasePath + "-wal"));
        _library.CopyTo(target);
        Assert.False(File.Exists(Path.Combine(target, "library.db-wal")));
        Assert.False(File.Exists(Path.Combine(target, "library.db-shm")));
        var copy = new Library(target);
        Assert.Equal(_library.GetContent(article.Id)!.BodyHtml, copy.GetContent(article.Id)!.BodyHtml);
    }

    [Fact]
    public void CopyRejectsChildWhenSourceHasTrailingSeparator()
    {
        var library = new Library(_library.RootDirectory + Path.DirectorySeparatorChar);
        Assert.Throws<IOException>(() => library.CopyTo(Path.Combine(library.RootDirectory, "child")));
    }

    [Theory]
    [InlineData("assets")]
    [InlineData(".")]
    [InlineData("..")]
    public void AccountNameCannotDeleteLibraryOrUnrelatedAssets(string name)
    {
        var article = Save(Article(account: name));
        var sentinel = Put(Path.Combine(_library.AssetsDirectory, "other", "keep.txt"), "keep");
        // Keep the destructive baseline inside this fixture even for '..'.
        using var databaseGuard = File.Open(_library.DatabasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        _library.DeleteAccountData(article.Biz);
        Assert.True(File.Exists(sentinel));
        Assert.True(File.Exists(_library.DatabasePath));
    }

    [Fact]
    public void AccountDeletionPreservesOtherAccountWithSameDisplayName()
    {
        var first = Save();
        var other = Save(Article("two", "other-biz"));
        var firstFile = Put(Path.Combine(ExportPaths.FolderFor(ExportRoot, first), "article.txt"), "first");
        var otherFile = Put(Path.Combine(ExportPaths.FolderFor(ExportRoot, other), "article.txt"), "other");
        _library.DeleteAccountData(first.Biz);
        Assert.True(File.Exists(otherFile));
        Assert.Equal("other", File.ReadAllText(otherFile));
        Assert.NotNull(_library.GetArticle(other.Id));
        Assert.False(File.Exists(firstFile));
    }

    [Fact]
    public void AccountDeletionPreservesAmbiguousLegacyExport()
    {
        var first = Save();
        Save(Article("two", "other-biz"));
        var file = Put(Path.Combine(ExportRoot, first.Account, "2024-05-01 相同标题", "article.html"), "shared legacy");
        _library.DeleteAccountData(first.Biz);
        Assert.Equal("shared legacy", File.ReadAllText(file));
    }

    [Fact]
    public void ArticleIdCannotTraverseAssetDeletionBoundary()
    {
        var article = Save(Article("../../victim"));
        var file = Put(Path.Combine(_root, "victim", "keep.txt"), "keep");
        _library.DeleteAccountData(article.Biz);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task DifferentArticlesWithSameDateAndTitleHaveIndependentExports()
    {
        var first = Save();
        var second = Save(Article("two"), "<p>第二篇</p>");
        var a = (await Export(first, null, ExportFormat.Text)).Items.Single();
        var b = (await Export(second, null, ExportFormat.Text)).Items.Single();
        Assert.True(a.Ok && b.Ok);
        Assert.NotEqual(a.Path, b.Path);
        Assert.Contains("新正文", File.ReadAllText(a.Path!));
        Assert.Contains("第二篇", File.ReadAllText(b.Path!));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("CON")]
    [InlineData("NUL.txt")]
    public async Task ExportHandlesDotAndWindowsDeviceAccountNames(string name)
    {
        var article = Save(Article(account: name));
        var result = (await Export(article, null, ExportFormat.Text)).Items.Single();
        Assert.True(result.Ok, result.Error);
        Assert.StartsWith(Path.GetFullPath(ExportRoot) + Path.DirectorySeparatorChar, Path.GetFullPath(result.Path!), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PdfAlwaysUsesCurrentContentAndPreservesExistingHtml()
    {
        var article = Save();
        var html = Put(Path.Combine(ExportPaths.FolderFor(ExportRoot, article), "article.html"), "old html");
        string? rendered = null;
        var renderer = new Renderer((input, output, _) =>
        {
            rendered = File.ReadAllText(input);
            File.WriteAllText(output, "%PDF-test");
            return Task.CompletedTask;
        });
        Assert.True((await Export(article, renderer, ExportFormat.Pdf)).Items.Single().Ok);
        Assert.Contains("新正文", rendered);
        Assert.Equal("old html", File.ReadAllText(html));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledPdfPreservesOldFilesAndCleansTemporaryFiles(bool cancel)
    {
        var article = Save();
        var folder = ExportPaths.FolderFor(ExportRoot, article);
        var pdf = Put(Path.Combine(folder, "article.pdf"), "old pdf");
        var html = Put(Path.Combine(folder, "article.html"), "old html");
        var renderer = new Renderer((_, output, _) =>
        {
            File.WriteAllText(output, "partial");
            return cancel ? Task.FromException(new OperationCanceledException()) : Task.FromException(new IOException("render failure"));
        });
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Export(article, renderer, ExportFormat.Pdf));
        else Assert.False((await Export(article, renderer, ExportFormat.Pdf)).Items.Single().Ok);
        Assert.Equal("old pdf", File.ReadAllText(pdf));
        Assert.Equal("old html", File.ReadAllText(html));
        Assert.Equal(2, Directory.GetFiles(folder).Length);
    }

    [Fact]
    public async Task PdfRendererMustProduceANonemptyOutput()
    {
        var article = Save();
        var renderer = new Renderer((_, _, _) => Task.CompletedTask);
        Assert.False((await Export(article, renderer, ExportFormat.Pdf)).Items.Single().Ok);
    }

    [Fact]
    public async Task WordExportEmbedsArchivedImage()
    {
        var path = Path.Combine(_library.AssetsDirectory, "one", "pixel.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII="));
        var article = Save(null, "<p><strong>正文</strong><img src='https://mmbiz.qpic.cn/a.png'></p>",
            new Asset("https://mmbiz.qpic.cn/a.png", "image", Path.GetRelativePath(_library.RootDirectory, path)));
        var item = (await Export(article, null, ExportFormat.Word)).Items.Single();
        Assert.True(item.Ok, item.Error);
        using var word = WordprocessingDocument.Open(item.Path!, false);
        Assert.Single(word.MainDocumentPart!.ImageParts);
        Assert.Empty(new OpenXmlValidator().Validate(word));
    }

    [Fact]
    public async Task AssetRefreshUpdatesNewExportWithoutChangingOldReferences()
    {
        var source = Put(Path.Combine(_library.AssetsDirectory, "one", "image.png"), "first bytes");
        var article = Save(null, "<img src='https://mmbiz.qpic.cn/a.png'>",
            new Asset("https://mmbiz.qpic.cn/a.png", "image", Path.GetRelativePath(_library.RootDirectory, source)));
        var first = (await Export(article, null, ExportFormat.Html)).Items.Single();
        var htmlBefore = File.ReadAllText(first.Path!);
        var assetDirectory = Path.Combine(Path.GetDirectoryName(first.Path!)!, "assets");
        var oldAsset = Directory.GetFiles(assetDirectory).Single();
        File.WriteAllText(source, "second bytes");
        await Export(article, null, ExportFormat.Html);
        Assert.NotEqual(htmlBefore, File.ReadAllText(first.Path!));
        Assert.Equal("first bytes", File.ReadAllText(oldAsset));
        Assert.Contains(Directory.GetFiles(assetDirectory), p => File.ReadAllText(p) == "second bytes");
    }

    [Fact]
    public async Task ExportRejectsAssetOutsideLibraryAssets()
    {
        var secret = Put(Path.Combine(_root, "private.txt"), "private");
        var article = Save(null, "<img src='https://mmbiz.qpic.cn/a.png'>", new Asset("https://mmbiz.qpic.cn/a.png", "image", secret));
        var item = (await Export(article, null, ExportFormat.Html)).Items.Single();
        Assert.False(item.Ok);
        Assert.DoesNotContain(Directory.GetFiles(ExportRoot, "*", SearchOption.AllDirectories), p => File.ReadAllText(p).Contains("private", StringComparison.Ordinal));
    }

    [Fact]
    public void WordPreservesBlocksAndTablesInsideArticleContainers()
    {
        var path = Path.Combine(_root, "nested.docx");
        var content = new ArticleContent("one", "<section><p>甲</p><p>乙</p><table><tr><th>列</th></tr><tr><td>值</td></tr></table><ul><li>项</li></ul></section>", DateTimeOffset.UtcNow, [], []);
        OfficeWriter.Word(path, Article(), content, new Dictionary<string, string>());
        using var word = WordprocessingDocument.Open(path, false);
        var body = word.MainDocumentPart!.Document!.Body!;
        Assert.Single(body.Elements<W.Table>());
        Assert.Contains(body.Elements<W.Paragraph>(), p => p.InnerText == "甲");
        Assert.Contains(body.Elements<W.Paragraph>(), p => p.InnerText == "乙");
        Assert.Contains(body.Elements<W.Paragraph>(), p => p.InnerText == "• 项");
        Assert.Empty(new OpenXmlValidator().Validate(word));
    }

    [Fact]
    public void MarkdownLocalizesImagesInsideTables()
    {
        var content = new ArticleContent("one", "<table><tr><td><img src='https://mmbiz.qpic.cn/a.png'></td></tr></table>", DateTimeOffset.UtcNow, [], []);
        var result = MarkupWriter.Markdown(Article(), content, new Dictionary<string, string> { ["https://mmbiz.qpic.cn/a.png"] = "assets/a.png" });
        Assert.Contains("assets/a.png", result);
        Assert.DoesNotContain("https://mmbiz.qpic.cn/a.png", result);
    }

    [Theory]
    [InlineData(ExportFormat.Html)]
    [InlineData(ExportFormat.Markdown)]
    [InlineData(ExportFormat.Text)]
    [InlineData(ExportFormat.Word)]
    public async Task ExportReplacesFileAtomicallyWhileExistingReaderKeepsOldBytes(ExportFormat format)
    {
        var article = Save();
        var first = (await Export(article, null, format)).Items.Single();
        Assert.True(first.Ok, first.Error);
        var oldBytes = File.ReadAllBytes(first.Path!);
        using var reader = File.Open(first.Path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Save(article, "<p>替换后的正文</p>");
        var replacement = (await Export(article, null, format)).Items.Single();
        Assert.True(replacement.Ok, replacement.Error);
        Assert.False(oldBytes.SequenceEqual(File.ReadAllBytes(first.Path!)));
        using var snapshot = new MemoryStream();
        reader.CopyTo(snapshot);
        Assert.Equal(oldBytes, snapshot.ToArray());
    }

    [Fact]
    public void FailedCopyDoesNotPublishPartialLibrary()
    {
        Save();
        var lockedPath = Put(Path.Combine(_library.AssetsDirectory, "locked.dat"), "locked");
        using var locked = File.Open(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var target = Path.Combine(_root, "failed-copy");
        Assert.Throws<IOException>(() => _library.CopyTo(target));
        Assert.False(Directory.Exists(target));
        Assert.Empty(Directory.GetDirectories(_root, "failed-copy.*.tmp"));
        Assert.NotNull(_library.GetArticle("one"));
    }

    [Fact]
    public void FailedDatabaseDeletionPreservesFiles()
    {
        var article = Save();
        var asset = Put(Path.Combine(_library.AssetsDirectory, article.Id, "keep.png"), "keep");
        var export = Put(Path.Combine(ExportPaths.FolderFor(ExportRoot, article), "article.txt"), "keep");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _library.DatabasePath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER reject_delete BEFORE DELETE ON articles BEGIN SELECT RAISE(ABORT, 'simulated failure'); END";
        command.ExecuteNonQuery();
        Assert.Throws<SqliteException>(() => _library.DeleteAccountData(article.Biz));
        Assert.NotNull(_library.GetArticle(article.Id));
        Assert.NotNull(_library.GetContent(article.Id));
        Assert.True(File.Exists(asset));
        Assert.True(File.Exists(export));
    }

    [Fact]
    public void QueryStateIsExactAndPendingJsonIsNotPartOfLibraryQuery()
    {
        var ready = Save();
        var partial = Save(Article("partial"));
        _library.SetState(partial.Id, ContentState.Partial);
        _library.UpsertArticles([Article("pending"), Article("failed") with { State = ContentState.Failed }]);
        Assert.Equal(ready.Id, _library.Query(new LibraryQuery(State: ContentState.Ready)).Items.Single().Id);
        Assert.Equal("pending", _library.Query(new LibraryQuery(State: ContentState.NotFetched)).Items.Single().Id);
        Assert.Equal(4, _library.Query(new LibraryQuery()).Total);
        Assert.Equal(2, _library.Accounts().Single().Saved);
        Assert.Empty(_library.Query(new LibraryQuery(Search: "%")).Items);
    }

    [Fact]
    public void CheckpointIsExplicitlyReplacedAndEnsureAccountDoesNotAdvanceIt()
    {
        Assert.Equal((0, false), _library.GetCheckpoint("biz"));
        _library.EnsureAccount("biz", "first");
        Assert.Null(_library.Accounts().Single().LastCollected);
        _library.SaveCheckpoint("biz", "first", 30, false);
        var collected = _library.Accounts().Single().LastCollected;
        Assert.NotNull(collected);
        _library.EnsureAccount("biz", "renamed");
        Save();
        Assert.Equal((30, false), _library.GetCheckpoint("biz"));
        Assert.Equal(collected, _library.Accounts().Single().LastCollected);
        _library.SaveCheckpoint("biz", "", 0, true);
        Assert.Equal((0, true), _library.GetCheckpoint("biz"));
        Assert.Equal("renamed", _library.GetAccountName("biz"));
    }
}
