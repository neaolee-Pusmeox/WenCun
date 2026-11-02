using System.Net;
using System.Net.Security;
using System.Globalization;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using WenCun.Core.Content;
using WenCun.Core.Domain;

namespace WenCun.Capture;

/// <summary>
/// 临时本地代理：只在用户同意后接管当前用户代理，只解密 mp.weixin.qq.com，
/// 从微信自己的请求里取一次阅读会话，然后立刻恢复网络。
/// </summary>
public sealed class CaptureProxy : IAsyncDisposable
{
    private static readonly string[] Inherited =
    [
        "uin", "key", "wxtoken", "x5", "devicetype", "version", "clientversion",
        "enterid", "user_article_role", "pass_ticket", "appmsg_token"
    ];

    private readonly ProxyServer _server;
    private readonly ExplicitProxyEndPoint _endpoint;
    private readonly LocalCaptureCertificate _certificate;
    private readonly string _journalPath;
    private readonly string? _expectedBiz;
    private readonly object _gate = new();
    private readonly Func<ProxySettings> _readProxy = SystemProxy.Read;
    private readonly Action<ProxySettings> _writeProxy = SystemProxy.Write;
    private ProxySettings? _original;
    private ProxySettings? _applied;
    private Session? _pending;
    private Timer? _grace;
    private int _stopped;
    private int _captured;
    private Task<IReadOnlyList<string>>? _stopTask;

    public CaptureProxy(string appRoot, string? expectedBiz = null)
    {
        _journalPath = CaptureJournal.PathFor(appRoot);
        _expectedBiz = expectedBiz;
        _certificate = LocalCaptureCertificate.LoadOrCreate();
        _server = new ProxyServer(userTrustRootCertificate: false, machineTrustRootCertificate: false, trustRootCertificateAsAdmin: false);
        _server.Logging.Enabled = false;
        _server.EnableHttp2 = false;
        _server.SetHttp3Enabled(false);
        _server.ForwardToUpstreamGateway = false;
        _server.EnableWinAuth = false;
        _server.NetworkFailureRetryAttempts = 0;
        _server.MaxConcurrentClientConnections = 64;
        _server.ConnectTimeOutSeconds = 15;
        _server.ConnectionTimeOutSeconds = 45;
        _server.ClientHeaderTimeoutSeconds = 15;
        _server.ResponseHeaderTimeoutSeconds = 30;
        _endpoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true)
        {
            GenericCertificate = _certificate.Certificate
        };
        _endpoint.BeforeTunnelConnectRequest += OnTunnel;
        _server.AfterResponse += OnResponse;
        _server.ServerCertificateValidationCallback += (_, e) =>
        {
            e.IsValid = e.SslPolicyErrors == SslPolicyErrors.None;
            return Task.CompletedTask;
        };
        _server.AddEndPoint(_endpoint);
    }

    public event Action<Session>? Captured;

    public int Port => _endpoint.Port;

    public void Start()
    {
        _original = _readProxy();
        try
        {
            _certificate.EnsureTrusted();
            _server.Start(changeSystemProxySettings: false);
            if (Port <= 0) throw new InvalidOperationException("代理没有获得可用端口。");
            var applied = ProxySettings.ForCapture(Port, _original.Bypass);
            CaptureJournal.Write(_journalPath, new CaptureJournalEntry(_original, applied.Server, DateTimeOffset.UtcNow));
            _applied = applied;
            _writeProxy(_applied);
        }
        catch
        {
            StopAsync().GetAwaiter().GetResult();
            throw;
        }
    }

    public Task<IReadOnlyList<string>> StopAsync()
    {
        lock (_gate)
        {
            Volatile.Write(ref _stopped, 1);
            _grace?.Dispose();
            _pending = null;
            return _stopTask ??= StopCoreAsync();
        }
    }

    private async Task<IReadOnlyList<string>> StopCoreAsync()
    {
        var warnings = new List<string>();
        try
        {
            if (_original is not null && _applied is not null)
            {
                var current = _readProxy();
                var restored = current.RestoreOriginal(_original, _applied);
                if (restored != current) _writeProxy(restored);
            }
        }
        catch (Exception ex) { warnings.Add("恢复代理失败：" + ex.Message); }
        try
        {
            if (_server.ProxyRunning) await _server.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception ex) { warnings.Add("停止本地代理失败：" + ex.Message); }
        if (warnings.Count == 0 && _applied is not null)
        {
            try { CaptureJournal.Delete(_journalPath); }
            catch (Exception ex) { warnings.Add("清理代理恢复记录失败：" + ex.Message); }
        }
        return warnings;
    }

    private Task OnTunnel(object sender, TunnelConnectSessionEventArgs e)
    {
        var uri = e.HttpClient.Request.RequestUri;
        e.DecryptSsl = Volatile.Read(ref _stopped) == 0 && uri.Port == 443 &&
                       uri.Host.Equals("mp.weixin.qq.com", StringComparison.OrdinalIgnoreCase);
        return Task.CompletedTask;
    }

    private Task OnResponse(object sender, SessionEventArgs e)
    {
        if (Volatile.Read(ref _stopped) != 0 || Volatile.Read(ref _captured) != 0) return Task.CompletedTask;
        var request = e.HttpClient.Request;
        var uri = request.RequestUri;
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            !uri.Host.Equals("mp.weixin.qq.com", StringComparison.OrdinalIgnoreCase)) return Task.CompletedTask;
        if (e.Exception is not null || e.HttpClient.Response.StatusCode is < 200 or >= 400) return Task.CompletedTask;
        var query = UrlRules.Query(uri);
        if (!query.TryGetValue("__biz", out var biz) || string.IsNullOrWhiteSpace(biz)) return Task.CompletedTask;
        if (_expectedBiz is not null && !string.Equals(_expectedBiz, biz, StringComparison.Ordinal)) return Task.CompletedTask;
        var cookie = Header(request, "Cookie");
        var setCookies = e.HttpClient.Response.Headers.GetHeaders("Set-Cookie")?.Select(header => header.Value) ?? [];
        cookie = MergeCookies(cookie, setCookies);
        if (string.IsNullOrWhiteSpace(cookie)) return Task.CompletedTask;
        var token = query.GetValueOrDefault("appmsg_token");
        if (string.IsNullOrWhiteSpace(token)) token = CookieValue(cookie, "appmsg_token");
        if (string.IsNullOrWhiteSpace(token)) return Task.CompletedTask;
        var passTicket = query.GetValueOrDefault("pass_ticket");
        if (string.IsNullOrWhiteSpace(passTicket)) passTicket = CookieValue(cookie, "pass_ticket");
        var session = new Session
        {
            Cookie = cookie,
            UserAgent = Header(request, "User-Agent") is { Length: > 0 } agent ? agent : "Mozilla/5.0",
            Biz = biz,
            AppMsgToken = token,
            PassTicket = passTicket ?? "",
            Parameters = ExtractParameters(uri),
            SampleUrl = uri.AbsoluteUri,
            CapturedAt = DateTimeOffset.UtcNow
        };
        // 文章页请求可能不带 uin/key；给微信自己的带签名请求留 3 秒窗口，
        // 有签名就用签名，没有就用第一个可用候选。
        var signed = false;
        lock (_gate)
        {
            if (Volatile.Read(ref _captured) != 0 || Volatile.Read(ref _stopped) != 0) return Task.CompletedTask;
            if (HasSignature(session.Parameters))
            {
                _captured = 1;
                _grace?.Dispose();
                signed = true;
            }
            else
            {
                _pending ??= session;
                _grace ??= new Timer(_ => FlushPending(), null, TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
            }
        }
        if (signed) Captured?.Invoke(session);
        return Task.CompletedTask;
    }

    private void FlushPending()
    {
        Session? session;
        lock (_gate)
        {
            if (Volatile.Read(ref _captured) != 0 || Volatile.Read(ref _stopped) != 0) return;
            session = _pending;
            _captured = 1;
        }
        if (session is not null) Captured?.Invoke(session);
    }

    private static bool HasSignature(string parameters) =>
        parameters.Contains("uin=", StringComparison.Ordinal) && parameters.Contains("key=", StringComparison.Ordinal);

    private static string Header(Request request, string name) =>
        request.Headers.GetHeaders(name)?.FirstOrDefault()?.Value ?? "";

    private static string? CookieValue(string cookie, string name) =>
        cookie.Split(';').Select(part => part.Trim().Split('=', 2))
            .Where(pair => pair.Length == 2 && pair[0] == name)
            .Select(pair => pair[1])
            .LastOrDefault();

    private static string MergeCookies(string cookie, IEnumerable<string> setCookies)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Trim().Split('=', 2);
            if (pair.Length == 2) map[pair[0]] = pair[1];
        }
        foreach (var header in setCookies)
        {
            var parts = header.Split(';');
            var pair = parts[0].Split('=', 2);
            if (pair.Length != 2 || pair[0].Trim().Length == 0) continue;
            long? maxAge = null;
            DateTimeOffset? expires = null;
            foreach (var part in parts.Skip(1))
            {
                var attribute = part.Trim().Split('=', 2);
                if (attribute.Length != 2) continue;
                if (attribute[0].Equals("Max-Age", StringComparison.OrdinalIgnoreCase) &&
                    long.TryParse(attribute[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var age)) maxAge = age;
                if (attribute[0].Equals("Expires", StringComparison.OrdinalIgnoreCase) &&
                    DateTimeOffset.TryParse(attribute[1], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)) expires = time;
            }
            if (maxAge.HasValue ? maxAge <= 0 : expires <= DateTimeOffset.UtcNow) map.Remove(pair[0].Trim());
            else map[pair[0].Trim()] = pair[1];
        }
        return string.Join("; ", map.Select(pair => pair.Key + "=" + pair.Value));
    }

    private static string ExtractParameters(Uri uri)
    {
        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2 || !Inherited.Contains(pair[0], StringComparer.Ordinal) || !seen.Add(pair[0])) continue;
            if (pair[1].Length is 0 or > 512) continue;
            if (!pair[1].All(c => char.IsAsciiLetterOrDigit(c) ||
                                  c is '%' or '.' or '_' or '-' or '~' or '*' or '+' or ':' or '@' or '!' or '(' or ')' or '\'' or '$' or ',' or ';')) continue;
            parts.Add(pair[0] + "=" + pair[1]);
        }
        parts.Sort(StringComparer.Ordinal);
        var result = string.Join('&', parts);
        return result.Length <= 4096 ? result : "";
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _grace?.Dispose();
        _server.Dispose();
        _certificate.Dispose();
    }
}
