using System.Net;
using System.Text.Json;
using WenCun.Core.Content;
using WenCun.Core.Domain;

namespace WenCun.Core.Net;

/// <summary>只做三件事：拉历史目录、取文章正文、下载图片。</summary>
public sealed class WeChatClient : IDisposable
{
    private const int PageSize = 10;
    private static readonly TimeSpan FeedInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RequestInterval = TimeSpan.FromMilliseconds(600);
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    internal WeChatClient(HttpClient http) => _http = http;

    public WeChatClient()
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(3)
        })
        { Timeout = TimeSpan.FromSeconds(60) };
    }

    public async Task<ArticlePage> FetchListAsync(Session session, int offset, CancellationToken ct)
    {
        var url = "https://mp.weixin.qq.com/mp/profile_ext?action=getmsg" +
                  $"&__biz={Uri.EscapeDataString(session.Biz)}&f=json&offset={offset}&count={PageSize}&is_ok=1&scene=124";
        url = AppendSessionParameters(url, session);
        if (session.AppMsgToken.Length > 0 && !url.Contains("appmsg_token=", StringComparison.Ordinal))
            url += "&appmsg_token=" + Uri.EscapeDataString(session.AppMsgToken);
        if (session.PassTicket.Length > 0 && !url.Contains("pass_ticket=", StringComparison.Ordinal))
            url += "&pass_ticket=" + Uri.EscapeDataString(session.PassTicket);
        var json = await SendAsync(url, session, ct, feed: true, 4 * 1024 * 1024);
        return ParsePage(json, offset, session.Account, session.Biz);
    }

    public Task<string> FetchArticleAsync(Session session, string url, CancellationToken ct) =>
        SendAsync(url, session, ct, feed: false, 12 * 1024 * 1024);

    public async Task<byte[]> DownloadAsync(string url, CancellationToken ct, int maxBytes = 20 * 1024 * 1024)
    {
        var uri = UrlRules.RequireAsset(url);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await PaceAsync(false, ct).ConfigureAwait(false);
            using var response = await SendValidatedAsync(uri, null, false, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new WenCunException(FailureKind.Network, $"图片下载失败（HTTP {(int)response.StatusCode}）。");
            return await ReadLimitedAsync(response.Content, maxBytes, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            throw new WenCunException(FailureKind.Network, "资源下载失败，请稍后重试。");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new WenCunException(FailureKind.Network, "资源下载超时，请稍后重试。");
        }
        finally { _gate.Release(); }
    }

    private async Task<string> SendAsync(string url, Session session, CancellationToken ct, bool feed, int maxBytes)
    {
        var uri = UrlRules.RequireWeChat(url);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await PaceAsync(feed, ct).ConfigureAwait(false);
            using var response = await SendValidatedAsync(uri, session, feed, ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new WenCunException(FailureKind.Session, "微信拒绝了当前会话，请重新采集。");
            if ((int)response.StatusCode == 429)
                throw new WenCunException(FailureKind.RateLimited, "微信限制了请求频率（HTTP 429），请稍后再试。");
            if (!response.IsSuccessStatusCode)
                throw new WenCunException(FailureKind.Network, $"微信请求失败（HTTP {(int)response.StatusCode}）。");
            var bytes = await ReadLimitedAsync(response.Content, maxBytes, ct).ConfigureAwait(false);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch (HttpRequestException)
        {
            throw new WenCunException(FailureKind.Network, "网络请求失败，请检查网络后重试。");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new WenCunException(FailureKind.Network, "微信请求超时，请稍后重试。");
        }
        finally { _gate.Release(); }
    }

    private async Task PaceAsync(bool feed, CancellationToken ct)
    {
        var wait = _lastRequest + (feed ? FeedInterval : RequestInterval) - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
        _lastRequest = DateTimeOffset.UtcNow;
    }

    private async Task<HttpResponseMessage> SendValidatedAsync(Uri uri, Session? session, bool feed, CancellationToken ct)
    {
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            uri = session is null ? UrlRules.RequireAsset(uri.AbsoluteUri) : UrlRules.RequireWeChat(uri.AbsoluteUri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", session?.UserAgent ?? "Mozilla/5.0");
            request.Headers.TryAddWithoutValidation("Referer", "https://mp.weixin.qq.com/");
            if (session is not null)
            {
                request.Headers.TryAddWithoutValidation("Cookie", session.Cookie);
                request.Headers.TryAddWithoutValidation("Accept", feed
                    ? "application/json, text/javascript, */*; q=0.01"
                    : "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            }
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new WenCunException(FailureKind.Protocol, "重定向缺少目标地址。");
            uri = new Uri(uri, location);
        }
        throw new WenCunException(FailureKind.Protocol, "重定向次数过多。");
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        if (content.Headers.ContentLength > maxBytes)
            throw new WenCunException(FailureKind.Unavailable, "响应内容超过大小上限。");
        await using var input = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > maxBytes)
                throw new WenCunException(FailureKind.Unavailable, "响应内容超过大小上限。");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static string AppendSessionParameters(string url, Session session)
    {
        if (string.IsNullOrWhiteSpace(session.Parameters)) return url;
        var existing = new HashSet<string>(StringComparer.Ordinal);
        var at = url.IndexOf('?', StringComparison.Ordinal);
        if (at >= 0)
            foreach (var part in url[(at + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
                existing.Add(part.Split('=', 2)[0]);
        var added = session.Parameters.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !existing.Contains(part.Split('=', 2)[0]))
            .ToArray();
        return added.Length == 0 ? url : url + "&" + string.Join('&', added);
    }

    public static ArticlePage ParsePage(string json, int offset, string account, string biz)
    {
        try { return ParsePageCore(json, offset, account, biz); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new WenCunException(FailureKind.Protocol, "历史列表结构无法识别，进度已保留。");
        }
    }

    private static ArticlePage ParsePageCore(string json, int offset, string account, string biz)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { throw new WenCunException(FailureKind.Protocol, "历史列表不是有效 JSON，可能被重定向到登录页。"); }
        using (document)
        {
            var root = document.RootElement;
            CheckResult(root);
            if (!root.TryGetProperty("general_msg_list", out var listProperty) || listProperty.ValueKind != JsonValueKind.String)
                throw new WenCunException(FailureKind.Protocol, "微信没有返回历史列表数据，可能触发了访问限制。");
            using var embedded = JsonDocument.Parse(listProperty.GetString()!);
            if (!embedded.RootElement.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
                throw new WenCunException(FailureKind.Protocol, "历史列表结构无法识别。");
            var articles = new List<Article>();
            foreach (var message in list.EnumerateArray())
            {
                if (!message.TryGetProperty("app_msg_ext_info", out var info))
                {
                    if (message.TryGetProperty("comm_msg_info", out var meta) && Number(meta, "type", -1) == 1) continue;
                    continue;
                }
                var time = message.TryGetProperty("comm_msg_info", out var common) ? Time(common, "datetime") : null;
                if (TryArticle(info, time, account, 1, out var head)) articles.Add(head);
                if (!info.TryGetProperty("multi_app_msg_item_list", out var multi) || multi.ValueKind != JsonValueKind.Array) continue;
                var index = 2;
                foreach (var child in multi.EnumerateArray())
                    if (TryArticle(child, time, account, index++, out var article)) articles.Add(article);
            }
            var more = Number(root, "can_msg_continue", 0) == 1;
            var next = Number(root, "next_offset", offset);
            if (more && next <= offset) throw new WenCunException(FailureKind.Protocol, "历史列表游标没有前进，已停止以免重复请求。");
            foreach (var article in articles)
                if (article.Biz.Length > 0 && article.Biz != biz)
                    throw new WenCunException(FailureKind.Protocol, "历史列表包含其他公众号的文章，进度未更新。");
            return new ArticlePage(articles, next, more);
        }
    }

    private static void CheckResult(JsonElement root)
    {
        if (root.TryGetProperty("base_resp", out var baseResponse)) root = baseResponse;
        var code = Number(root, "ret", int.MinValue);
        if (code == 0) return;
        if (code is 200013 or 429)
            throw new WenCunException(FailureKind.RateLimited, "微信限制了请求频率，请稍后再试。");
        if (code == -6)
            throw new WenCunException(FailureKind.RateLimited, "微信拒绝了历史列表请求（-6），可能触发频率限制，进度已保留。");
        if (code is -3 or 200003 or 200004 or 40101 or 40001)
            throw new WenCunException(FailureKind.Session, "会话未通过微信验证，请重新采集。");
        if (code == int.MinValue)
            throw new WenCunException(FailureKind.Protocol, "响应缺少接口状态，可能遇到登录或验证码页面。");
        throw new WenCunException(FailureKind.Protocol, $"微信接口返回错误代码 {code}。");
    }

    private static bool TryArticle(JsonElement item, DateTimeOffset? time, string account, int fallbackIndex, out Article article)
    {
        article = null!;
        var url = Text(item, "content_url");
        if (string.IsNullOrWhiteSpace(url)) return false;
        url = WebUtility.HtmlDecode(url).Replace("\\/", "/", StringComparison.Ordinal);
        string canonical;
        try { canonical = UrlRules.CanonicalArticleUrl(url); }
        catch (WenCunException) { return false; }
        var query = UrlRules.Query(new Uri(canonical));
        article = new Article
        {
            Id = UrlRules.ArticleId(canonical),
            Url = canonical,
            Biz = query.GetValueOrDefault("__biz", ""),
            Account = account,
            Title = WebUtility.HtmlDecode(Text(item, "title")).Trim(),
            Author = Text(item, "author") is { Length: > 0 } author ? author : Text(item, "author_name"),
            Index = int.TryParse(query.GetValueOrDefault("idx"), out var index) ? index : fallbackIndex,
            PublishedAt = time,
            State = ContentState.NotFetched
        };
        return true;
    }

    private static string Text(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString()
            : "";

    private static int Number(JsonElement root, string key, int fallback) =>
        int.TryParse(Text(root, key), out var value) ? value : fallback;

    private static DateTimeOffset? Time(JsonElement root, string key) =>
        long.TryParse(Text(root, key), out var seconds) && seconds is > 0 and < 253402300800
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
