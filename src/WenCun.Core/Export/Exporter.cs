using System.Text;
using System.Security.Cryptography;
using WenCun.Core.Content;
using WenCun.Core.Domain;
using WenCun.Core.Storage;

namespace WenCun.Core.Export;

public interface IPdfRenderer
{
    Task RenderPdfAsync(string htmlPath, string pdfPath, CancellationToken ct);
}

public sealed class Exporter(Library library, IPdfRenderer? renderer = null)
{
    public async Task<ExportResult> ExportAsync(ExportRequest request, IProgress<string>? progress, CancellationToken ct)
    {
        var items = new List<ExportItem>();
        var root = Path.GetFullPath(request.Directory);
        ct.ThrowIfCancellationRequested();
        FileBoundary.RequireNoLinks(root);
        Directory.CreateDirectory(root);
        var formats = request.Formats.Distinct().ToArray();
        var targets = request.Articles.DistinctBy(article => article.Id).ToArray();
        var done = 0;
        foreach (var requested in targets)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"正在导出 {++done}/{targets.Length}：{requested.Title}");
            var article = library.GetArticle(requested.Id) ?? requested;
            var content = library.GetContent(article.Id);
            if (content is null)
            {
                foreach (var format in formats)
                    items.Add(new ExportItem(article.Id, article.Title, format, false, null, "正文尚未保存，无法导出。"));
                continue;
            }
            try
            {
                var folder = FileBoundary.RequireWithin(root, ExportPaths.FolderFor(root, article));
                Directory.CreateDirectory(folder);
                var map = CopyAssets(content, folder);
                foreach (var format in formats)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var path = format switch
                        {
                            ExportFormat.Html => WriteText(Path.Combine(folder, "article.html"), MarkupWriter.Html(article, content, map)),
                            ExportFormat.Markdown => WriteText(Path.Combine(folder, "article.md"), MarkupWriter.Markdown(article, content, map)),
                            ExportFormat.Text => WriteText(Path.Combine(folder, "article.txt"), MarkupWriter.Text(article, content)),
                            ExportFormat.Word => WriteWord(article, content, map, folder),
                            ExportFormat.Pdf => await WritePdfAsync(article, content, map, folder, ct).ConfigureAwait(false),
                            _ => throw new WenCunException(FailureKind.Input, "未知导出格式。")
                        };
                        items.Add(new ExportItem(article.Id, article.Title, format, true, path, null));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (ex is WenCunException or IOException or UnauthorizedAccessException)
                    {
                        items.Add(new ExportItem(article.Id, article.Title, format, false, null, ex.Message));
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                foreach (var format in formats)
                    items.Add(new ExportItem(article.Id, article.Title, format, false, null, ex.Message));
            }
        }
        return new ExportResult(items);
    }

    private static string WriteText(string path, string text)
    {
        return FileBoundary.Write(path, temporary => File.WriteAllText(temporary, text, new UTF8Encoding(false)));
    }

    private static string WriteWord(Article article, ArticleContent content, IReadOnlyDictionary<string, string> map, string folder)
    {
        var path = Path.Combine(folder, "article.docx");
        var absoluteAssets = map.ToDictionary(pair => pair.Key, pair => Path.Combine(folder, pair.Value), StringComparer.Ordinal);
        return FileBoundary.Write(path, temporary => OfficeWriter.Word(temporary, article, content, absoluteAssets));
    }

    private async Task<string> WritePdfAsync(Article article, ArticleContent content, IReadOnlyDictionary<string, string> map, string folder, CancellationToken ct)
    {
        if (renderer is null) throw new WenCunException(FailureKind.Renderer, "未启用 PDF 渲染器。");
        var pdfPath = Path.Combine(folder, "article.pdf");
        FileBoundary.RequireNoLinks(pdfPath);
        var htmlPath = FileBoundary.TemporarySibling(Path.Combine(folder, "render")) + ".html";
        var temporaryPdf = FileBoundary.TemporarySibling(pdfPath);
        try
        {
            File.WriteAllText(htmlPath, MarkupWriter.Html(article, content, map), new UTF8Encoding(false));
            await renderer.RenderPdfAsync(htmlPath, temporaryPdf, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(temporaryPdf) || new FileInfo(temporaryPdf).Length == 0)
                throw new WenCunException(FailureKind.Renderer, "PDF 渲染器未生成有效文件。");
            FileBoundary.Publish(temporaryPdf, pdfPath);
            return pdfPath;
        }
        finally
        {
            File.Delete(htmlPath);
            File.Delete(temporaryPdf);
        }
    }

    private IReadOnlyDictionary<string, string> CopyAssets(ArticleContent content, string folder)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var asset in content.Assets)
        {
            if (asset.LocalPath is null) continue;
            var source = FileBoundary.RequireWithin(library.AssetsDirectory, Path.Combine(library.RootDirectory, asset.LocalPath));
            if (!File.Exists(source)) continue;
            var directory = FileBoundary.RequireWithin(folder, Path.Combine(folder, "assets"));
            Directory.CreateDirectory(directory);
            var bytes = File.ReadAllBytes(source);
            var name = Convert.ToHexStringLower(SHA256.HashData(bytes)) + Path.GetExtension(source);
            var target = Path.Combine(directory, name);
            FileBoundary.RequireNoLinks(target);
            if (!File.Exists(target)) FileBoundary.Write(target, temporary => File.WriteAllBytes(temporary, bytes));
            map[asset.Source] = "assets/" + name;
        }
        return map;
    }
}
