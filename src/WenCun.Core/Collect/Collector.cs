using System.Security.Cryptography;
using System.Text;
using WenCun.Core.Content;
using WenCun.Core.Domain;
using WenCun.Core.Export;
using WenCun.Core.Net;
using WenCun.Core.Pending;
using WenCun.Core.Storage;

namespace WenCun.Core.Collect;

public sealed class Collector(Library library, WeChatClient client, PendingStore pending,
    SaveOptions? options = null, IPdfRenderer? renderer = null)
{
    private readonly ArticleParser _parser = new();
    private readonly SaveOptions _options = options ?? SaveOptions.Default;

    /// <summary>只探明目录，写入待采集清单；不保存正文、不写文库。</summary>
    public async Task<int> DiscoverAsync(Session session, string biz, string account,
        IProgress<CollectProgress>? progress, CancellationToken ct)
    {
        var existing = pending.Load(biz);
        if (string.IsNullOrWhiteSpace(account) || account == biz) account = existing?.Account ?? "";
        if (string.IsNullOrWhiteSpace(account) || account == biz) account = library.GetAccountName(biz) ?? "";
        if (account == biz) account = "";
        if (string.IsNullOrWhiteSpace(account) && TryArticleUrl(session.SampleUrl, out var sampleUrl))
        {
            try
            {
                var html = await client.FetchArticleAsync(session, sampleUrl, ct).ConfigureAwait(false);
                account = ArticleParser.ReadAccountName(html);
            }
            catch (WenCunException)
            {
                // 示例文章不可用时，再从目录中的第一篇文章读取名称。
            }
        }
        var complete = existing?.Complete ?? false;
        var current = complete ? 0 : existing?.Offset ?? 0;
        int pages = 0, found = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await client.FetchListAsync(session with { Biz = biz, Account = account }, current, ct).ConfigureAwait(false);
            if (pages == 0 && string.IsNullOrWhiteSpace(account) && page.Articles.FirstOrDefault() is { } first)
            {
                try
                {
                    var html = await client.FetchArticleAsync(session, first.Url, ct).ConfigureAwait(false);
                    account = ArticleParser.ReadAccountName(html);
                }
                catch (WenCunException)
                {
                    // 名称探测失败不丢弃已取得的目录。
                }
            }
            var before = pending.Load(biz)?.Articles.Count ?? 0;
            var discovered = page.Articles.Where(article => library.GetContent(article.Id) is null).Select(article => new PendingArticle(
                article.Id,
                string.IsNullOrWhiteSpace(article.Biz) ? biz : article.Biz,
                string.IsNullOrWhiteSpace(article.Account) ? account : article.Account,
                article.Title,
                article.Author,
                article.Url,
                article.PublishedAt,
                DateTimeOffset.UtcNow)).ToArray();
            var merged = pending.Merge(biz, account, discovered, page.NextOffset, !page.HasMore);
            var added = merged.Articles.Count - before;
            pages++;
            found += page.Articles.Count;
            current = page.NextOffset;
            progress?.Report(new CollectProgress("目录", pages, found, merged.Articles.Count, $"第 {pages} 页，新增 {added} 篇"));
            if (!page.HasMore) break;
            if (complete && page.Articles.Count > 0 && added == 0) break;
        }
        if (complete) pending.Merge(biz, account, [], 0, true);
        var total = pending.Load(biz)?.Articles.Count ?? 0;
        progress?.Report(new CollectProgress("完成", pages, found, total, $"已探明 {total} 篇待采集"));
        return total;
    }

    /// <summary>保存用户选中的待采集文章：取正文、写文库、按设置导出，成功后从清单移除。</summary>
    public async Task<int> SaveSelectedAsync(Session session, string biz, IReadOnlyList<PendingArticle> selected,
        IProgress<CollectProgress>? progress, CancellationToken ct)
    {
        var saved = 0;
        foreach (var item in selected)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var article = new Article
                {
                    Id = item.Id,
                    Url = item.Url,
                    Biz = item.Biz,
                    Account = item.Account,
                    Title = item.Title,
                    Author = item.Author,
                    PublishedAt = item.PublishedAt,
                    State = ContentState.NotFetched
                };
                var parsed = await FetchBodyAsync(article, session, ct).ConfigureAwait(false);
                pending.RemoveArticles(biz, [item.Id]);
                if (!string.IsNullOrWhiteSpace(parsed.Account)) pending.SetAccountName(biz, parsed.Account);
                saved++;
                progress?.Report(new CollectProgress("保存", 0, selected.Count, saved, parsed.Title));
            }
            catch (WenCunException ex) when (ex.Kind is FailureKind.Session or FailureKind.RateLimited)
            {
                throw;
            }
            catch (Exception ex) when (ex is WenCunException or IOException)
            {
                pending.SetError(biz, item.Id, ex.Message);
                progress?.Report(new CollectProgress("保存", 0, selected.Count, saved, $"失败：{item.Title}（{ex.Message}）"));
            }
        }
        return saved;
    }

    public async Task<Article> FetchBodyAsync(Article article, Session session, CancellationToken ct)
    {
        var html = await client.FetchArticleAsync(session, article.Url, ct).ConfigureAwait(false);
        var parsed = _parser.Parse(html, article);
        return await SaveParsedAsync(parsed, article, ct).ConfigureAwait(false);
    }

    private async Task<Article> SaveParsedAsync(ParsedArticle parsed, Article? provisional, CancellationToken ct)
    {
        var article = parsed.Article;
        var content = parsed.Content;
        content = _options.Images switch
        {
            ImageMode.None => content with { BodyHtml = HtmlSanitizer.StripImages(content.BodyHtml), Assets = [] },
            ImageMode.Link => content with { Assets = content.Assets.Select(asset => asset with { LocalPath = null }).ToArray() },
            _ => content with { Assets = await DownloadAssetsAsync(content, ct).ConfigureAwait(false) }
        };
        ct.ThrowIfCancellationRequested();
        library.UpsertArticles([article with { State = ContentState.NotFetched }]);
        library.SaveContent(content, article.State);
        if (provisional is not null && provisional.Id != article.Id) library.DeleteArticles([provisional.Id]);
        library.EnsureAccount(article.Biz, article.Account);
        if (_options.Formats.Count > 0)
        {
            var exporter = new Exporter(library, renderer);
            var result = await exporter.ExportAsync(
                new ExportRequest([article], Path.Combine(library.RootDirectory, "articles"), _options.Formats),
                null, ct).ConfigureAwait(false);
            var failed = result.Items.Where(item => !item.Ok).ToArray();
            library.SetExportResult(article.Id,
                failed.Length == 0 ? ExportState.Succeeded : ExportState.Failed,
                failed.Length == 0 ? null : string.Join("；", failed.Select(item => item.Format + "：" + item.Error)));
        }
        else library.SetExportResult(article.Id, ExportState.None, null);
        return article;
    }

    private async Task<IReadOnlyList<Asset>> DownloadAssetsAsync(ArticleContent content, CancellationToken ct)
    {
        var directory = Path.Combine(library.AssetsDirectory, content.ArticleId);
        var result = new List<Asset>();
        foreach (var asset in content.Assets)
        {
            try
            {
                var bytes = await client.DownloadAsync(asset.Source, ct).ConfigureAwait(false);
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, Hash(asset.Source)[..16] + Extension(asset.Source, bytes));
                await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
                result.Add(asset with { LocalPath = Path.GetRelativePath(library.RootDirectory, path) });
            }
            catch (Exception ex) when (ex is WenCunException or IOException or UnauthorizedAccessException)
            {
                result.Add(asset with { Error = ex.Message });
            }
        }
        return result;
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool TryArticleUrl(string url, out string canonical)
    {
        canonical = "";
        if (string.IsNullOrWhiteSpace(url)) return false;
        try
        {
            var uri = new Uri(url);
            if (uri.AbsolutePath != "/s" && !uri.AbsolutePath.StartsWith("/s/", StringComparison.Ordinal)) return false;
            canonical = UrlRules.CanonicalArticleUrl(url);
            return true;
        }
        catch (Exception ex) when (ex is UriFormatException or WenCunException)
        {
            return false;
        }
    }

    private static string Extension(string source, byte[] bytes)
    {
        var extension = Path.GetExtension(new Uri(source).AbsolutePath).ToLowerInvariant();
        if (extension is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".mp3" or ".m4a" or ".aac" or ".wav")
            return extension;
        if (bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8) return ".jpg";
        if (bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return ".png";
        if (bytes.Length > 3 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46) return ".gif";
        if (bytes.Length > 12 && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50) return ".webp";
        return ".bin";
    }
}
