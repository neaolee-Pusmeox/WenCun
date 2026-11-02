using DocumentFormat.OpenXml.Packaging;
using WenCun.Core.Domain;
using WenCun.Core.Export;

namespace WenCun.Tests;

public sealed class ExportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "WenCunExportTests", Guid.NewGuid().ToString("N"));

    public ExportTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private static Article Article() => new()
    {
        Id = "1",
        Url = "https://mp.weixin.qq.com/s?__biz=MzA1&mid=1&idx=1",
        Biz = "MzA1",
        Account = "测试号",
        Title = "测试文章",
        Author = "作者甲",
        PublishedAt = DateTimeOffset.Parse("2024-05-01T10:00:00+08:00"),
        State = ContentState.Ready
    };

    private static ArticleContent Content() => new("1",
        "<p>正文<strong>加粗</strong></p><img src=\"https://mmbiz.qpic.cn/a.jpg\" alt=\"图\">",
        DateTimeOffset.UtcNow, [], []);

    [Fact]
    public void WritesHtmlMarkdownAndText()
    {
        var map = new Dictionary<string, string> { ["https://mmbiz.qpic.cn/a.jpg"] = "assets/a.jpg" };
        var html = MarkupWriter.Html(Article(), Content(), map);
        Assert.Contains("测试文章", html);
        Assert.Contains("assets/a.jpg", html);
        Assert.Contains("正文", html);

        var markdown = MarkupWriter.Markdown(Article(), Content(), map);
        Assert.Contains("# 测试文章", markdown);
        Assert.Contains("**加粗**", markdown);
        Assert.Contains("assets/a.jpg", markdown);

        var text = MarkupWriter.Text(Article(), Content());
        Assert.Contains("测试文章", text);
        Assert.Contains("正文", text);
    }

    [Fact]
    public void KeepsRemoteImagesWhenNotDownloaded()
    {
        var content = new ArticleContent("1",
            "<p>正文</p><img src=\"https://mmbiz.qpic.cn/a.jpg\" alt=\"图\">",
            DateTimeOffset.UtcNow, [], []);
        var map = new Dictionary<string, string>();
        var html = MarkupWriter.Html(Article(), content, map);
        Assert.Contains("https://mmbiz.qpic.cn/a.jpg", html);
        var markdown = MarkupWriter.Markdown(Article(), content, map);
        Assert.Contains("https://mmbiz.qpic.cn/a.jpg", markdown);
    }

    [Fact]
    public void WritesWordDocument()
    {
        var path = Path.Combine(_directory, "article.docx");
        OfficeWriter.Word(path, Article(), Content(), new Dictionary<string, string>());
        Assert.True(new FileInfo(path).Length > 0);
        using var document = WordprocessingDocument.Open(path, false);
        Assert.NotNull(document.MainDocumentPart);
        Assert.Contains("测试文章", document.MainDocumentPart!.Document!.Body!.InnerText);
    }

    [Fact]
    public void ExportPathUsesAccountAndDate()
    {
        var article = Article() with { Title = "标题:测试" };
        var path = ExportPaths.FolderFor(Path.Combine(_directory, "articles"), article);
        Assert.Contains("测试号", path);
        Assert.Contains("2024-05-01", path);
        Assert.Contains("标题 测试", path);
    }
}
