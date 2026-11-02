using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;
using WenCun.Capture;
using WenCun.Core.Domain;

namespace WenCun.Tests;

public sealed class CaptureRegressionTests
{
    private const string SignedUrl = "https://mp.weixin.qq.com/s?__biz=test-biz&uin=123&key=test-key";

    [Theory]
    [InlineData("http://mp.weixin.qq.com/s?__biz=test-biz&uin=123&key=test-key", 200)]
    [InlineData("https://mp.weixin.qq.com:8443/s?__biz=test-biz&uin=123&key=test-key", 200)]
    [InlineData("https://user@mp.weixin.qq.com/s?__biz=test-biz&uin=123&key=test-key", 200)]
    [InlineData(SignedUrl, 401)]
    [InlineData(SignedUrl, 403)]
    [InlineData(SignedUrl, 500)]
    public async Task UntrustedOrFailedResponsesCannotCapture(string url, int status)
    {
        using var fixture = new CaptureFixture();
        await fixture.Response(url, "appmsg_token=test-token", status);
        fixture.Flush();
        Assert.Empty(fixture.Sessions);
    }

    [Fact]
    public async Task ResponseCanEstablishTheFirstCookieSession()
    {
        using var fixture = new CaptureFixture();
        await fixture.Response(SignedUrl, "", 200, "appmsg_token=new-token; Path=/; Secure");
        Assert.Equal("new-token", Assert.Single(fixture.Sessions).AppMsgToken);
    }

    [Fact]
    public async Task EmptyQueryTokenFallsBackToUsableCookie()
    {
        using var fixture = new CaptureFixture();
        await fixture.Response(SignedUrl + "&appmsg_token=&pass_ticket=", "appmsg_token=test-token; pass_ticket=ticket");
        var session = Assert.Single(fixture.Sessions);
        Assert.Equal("test-token", session.AppMsgToken);
        Assert.Equal("ticket", session.PassTicket);
    }

    [Theory]
    [InlineData("appmsg_token=deleted; Max-Age=0; Path=/")]
    [InlineData("appmsg_token=deleted; max-age=-1; Path=/")]
    [InlineData("appmsg_token=deleted; Expires=Thu, 01 Jan 1970 00:00:00 GMT; Path=/")]
    public async Task ExpiredResponseCookieCannotBecomeCredentials(string header)
    {
        using var fixture = new CaptureFixture();
        await fixture.Response(SignedUrl, "appmsg_token=old-token", 200, header);
        fixture.Flush();
        Assert.Empty(fixture.Sessions);
    }

    [Fact]
    public async Task PositiveMaxAgeOverridesOldExpires()
    {
        using var fixture = new CaptureFixture();
        await fixture.Response(SignedUrl, "appmsg_token=old-token", 200,
            "appmsg_token=new-token; Max-Age=3600; Expires=Thu, 01 Jan 1970 00:00:00 GMT; Path=/");
        Assert.Equal("new-token", Assert.Single(fixture.Sessions).AppMsgToken);
    }

    [Fact]
    public async Task SignedCandidateWinsOnceAndExpectedBizIsEnforced()
    {
        using var fixture = new CaptureFixture("test-biz");
        await fixture.Response("https://mp.weixin.qq.com/s?__biz=other&uin=1&key=k", "appmsg_token=other");
        await fixture.Response("https://mp.weixin.qq.com/s?__biz=test-biz", "appmsg_token=unsigned");
        Assert.Empty(fixture.Sessions);
        await fixture.Response(SignedUrl, "appmsg_token=signed");
        fixture.Flush();
        await fixture.Response(SignedUrl, "appmsg_token=later");
        Assert.Equal("signed", Assert.Single(fixture.Sessions).AppMsgToken);
    }

    [Fact]
    public async Task StoppingDiscardsPendingSession()
    {
        using var fixture = new CaptureFixture();
        await fixture.Response("https://mp.weixin.qq.com/s?__biz=test-biz", "appmsg_token=unsigned");
        Assert.Empty(await fixture.Proxy.StopAsync());
        fixture.Flush();
        await fixture.Response(SignedUrl, "appmsg_token=later");
        Assert.Empty(fixture.Sessions);
    }

    [Theory]
    [InlineData("https=127.0.0.1:12345", 3, "local", "")]
    [InlineData("http=proxy:80;https=127.0.0.1:1234", 3, "local", "")]
    public async Task RestoreDoesNotOverwriteSubsequentUserSettings(string server, int flags, string bypass, string pac)
    {
        using var fixture = new CaptureFixture();
        fixture.Current = new(flags, server, bypass, pac);
        fixture.PrepareJournal();
        Assert.Null(CaptureJournal.Recover(fixture.Root, () => fixture.Current, fixture.WriteProxy));
        Assert.Empty(fixture.Writes);
        fixture.PrepareJournal();
        Assert.Empty(await fixture.Proxy.StopAsync());
        Assert.Empty(fixture.Writes);
    }

    [Theory]
    [InlineData(false, 1, "local", "")]
    [InlineData(true, 1, "local", "")]
    [InlineData(false, 3, "changed", "")]
    [InlineData(true, 3, "changed", "")]
    [InlineData(false, 7, "local", "https://proxy.test/pac")]
    [InlineData(true, 7, "local", "https://proxy.test/pac")]
    public async Task RestoreRemovesOurEndpointButPreservesUserChanges(bool crashRecovery, int flags, string bypass, string pac)
    {
        using var fixture = new CaptureFixture();
        fixture.Current = fixture.Applied with { Flags = flags, Bypass = bypass, AutoConfigUrl = pac };
        fixture.PrepareJournal();
        if (crashRecovery)
            Assert.Null(CaptureJournal.Recover(fixture.Root, () => fixture.Current, fixture.WriteProxy));
        else
            Assert.Empty(await fixture.Proxy.StopAsync());
        var restored = Assert.Single(fixture.Writes);
        Assert.Equal(fixture.Original.Server, restored.Server);
        Assert.Equal(flags == fixture.Applied.Flags ? fixture.Original.Flags : flags, restored.Flags);
        Assert.Equal(bypass, restored.Bypass);
        Assert.Equal(pac, restored.AutoConfigUrl);
    }

    [Fact]
    public async Task RestoreRecognizesOurExactSettings()
    {
        using var fixture = new CaptureFixture();
        fixture.PrepareJournal();
        Assert.Empty(await fixture.Proxy.StopAsync());
        Assert.Equal(fixture.Original, Assert.Single(fixture.Writes));
        Assert.False(File.Exists(fixture.JournalPath));
        fixture.Writes.Clear();
        fixture.Current = fixture.Applied;
        fixture.PrepareJournal();
        Assert.Null(CaptureJournal.Recover(fixture.Root, () => fixture.Current, fixture.WriteProxy));
        Assert.Equal(fixture.Original, Assert.Single(fixture.Writes));
    }

    [Fact]
    public async Task RepeatedStopDoesNotHideRecoveryFailure()
    {
        using var fixture = new CaptureFixture();
        fixture.PrepareJournal();
        fixture.FailWrite = true;
        var first = await fixture.Proxy.StopAsync();
        Assert.NotEmpty(first);
        Assert.True(File.Exists(fixture.JournalPath));
        Assert.Equal(first, await fixture.Proxy.StopAsync());
    }

    [Fact]
    public async Task NeverStartedProxyDoesNotDeleteAnExistingRecoveryJournal()
    {
        using var fixture = new CaptureFixture();
        CaptureJournal.Write(fixture.JournalPath, new(fixture.Original, fixture.Applied.Server, DateTimeOffset.UtcNow));
        Assert.Empty(await fixture.Proxy.StopAsync());
        Assert.True(File.Exists(fixture.JournalPath));
    }

    [Fact]
    public void NewCaptureCannotReplaceAnUnrecoveredJournal()
    {
        using var fixture = new CaptureFixture();
        fixture.PrepareJournal();
        var original = File.ReadAllText(fixture.JournalPath);
        Assert.Throws<IOException>(() => CaptureJournal.Write(fixture.JournalPath,
            new(new(1, "other", "", ""), "https=127.0.0.1:2345", DateTimeOffset.UtcNow)));
        Assert.Equal(original, File.ReadAllText(fixture.JournalPath));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("null")]
    public void InvalidRecoveryJournalIsPreservedAndReported(string contents)
    {
        using var fixture = new CaptureFixture();
        File.WriteAllText(fixture.JournalPath, contents);
        var warning = CaptureJournal.Recover(fixture.Root, () => throw new InvalidOperationException("must not read proxy"), fixture.WriteProxy);
        Assert.False(string.IsNullOrWhiteSpace(warning));
        Assert.Equal(contents, File.ReadAllText(fixture.JournalPath));
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public void LockedRecoveryJournalReturnsWarning()
    {
        using var fixture = new CaptureFixture();
        fixture.PrepareJournal();
        using var locked = new FileStream(fixture.JournalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var warning = CaptureJournal.Recover(fixture.Root, () => fixture.Current, fixture.WriteProxy);
        Assert.False(string.IsNullOrWhiteSpace(warning));
        Assert.Empty(fixture.Writes);
    }

    [Theory]
    [InlineData("CN=unrelated.example, O=WenCun Local Capture", false)]
    [InlineData("CN=mp.weixin.qq.com, O=WenCun Local Capture Backup", false)]
    [InlineData("CN=mp.weixin.qq.com, OU=WenCun, O=WenCun Local Capture", false)]
    [InlineData(LocalCaptureCertificate.SubjectName, true)]
    [InlineData("CN=mp.weixin.qq.com, O=WenCun Temporary Capture", true)]
    public void CertificateOwnershipRequiresExactIdentity(string subject, bool owned)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        Assert.Equal(owned, InvokeStatic<bool>(typeof(LocalCaptureCertificate), "IsOwned", certificate));
    }

    private static T InvokeStatic<T>(Type type, string name, params object[] arguments) =>
        (T)type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, arguments)!;

    // Bypass constructors that touch certificate stores. Only in-memory response handlers and
    // fake proxy read/write delegates run; no listeners, WinINET or persistent keys are used.
    private sealed class CaptureFixture : IDisposable
    {
        public CaptureProxy Proxy { get; } = Uninitialized<CaptureProxy>();
        public string Root { get; } = Path.Combine(AppContext.BaseDirectory, "capture-fixtures", Guid.NewGuid().ToString("N"));
        public string JournalPath => CaptureJournal.PathFor(Root);
        public ProxySettings Original { get; } = new(1, "", "local", "");
        public ProxySettings Applied { get; } = ProxySettings.ForCapture(1234, "local");
        public ProxySettings Current { get; set; } = ProxySettings.ForCapture(1234, "local");
        public List<ProxySettings> Writes { get; } = [];
        public List<Session> Sessions { get; } = [];
        public bool FailWrite { get; set; }

        public CaptureFixture(string? expectedBiz = null)
        {
            Directory.CreateDirectory(Root);
            SetField(Proxy, "_gate", new object());
            SetField(Proxy, "_journalPath", JournalPath);
            SetField(Proxy, "_expectedBiz", expectedBiz);
            SetField(Proxy, "_readProxy", (Func<ProxySettings>)(() => Current));
            SetField(Proxy, "_writeProxy", (Action<ProxySettings>)WriteProxy);
            SetField(Proxy, "_server", Uninitialized<ProxyServer>());
            Proxy.Captured += Sessions.Add;
        }

        public void WriteProxy(ProxySettings settings)
        {
            if (FailWrite) throw new IOException("simulated write failure");
            Writes.Add(settings);
            Current = settings;
        }

        public void PrepareJournal()
        {
            SetField(Proxy, "_original", Original);
            SetField(Proxy, "_applied", Applied);
            CaptureJournal.Write(JournalPath, new(Original, Applied.Server, DateTimeOffset.UtcNow));
        }

        public async Task Response(string url, string cookie, int status = 200, params string[] setCookies)
        {
            var request = new Request { RequestUri = new Uri(url), Method = "GET" };
            request.Headers.AddHeader("Cookie", cookie);
            var response = new Response { StatusCode = status };
            foreach (var value in setCookies) response.Headers.AddHeader("Set-Cookie", value);
            var client = Uninitialized<HttpWebClient>();
            SetField(client, "<Request>k__BackingField", request);
            SetField(client, "response", response);
            var args = Uninitialized<SessionEventArgs>();
            SetField(args, "<HttpClient>k__BackingField", client);
            await (Task)typeof(CaptureProxy).GetMethod("OnResponse", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Proxy, [this, args])!;
        }

        public void Flush() => typeof(CaptureProxy).GetMethod("FlushPending", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Proxy, null);

        public void Dispose()
        {
            SetField(Proxy, "_stopped", 1);
            (typeof(CaptureProxy).GetField("_grace", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Proxy) as Timer)?.Dispose();
            Directory.Delete(Root, recursive: true);
        }
    }

    private static T Uninitialized<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private static void SetField(object instance, string name, object? value)
    {
        for (var type = instance.GetType(); type is not null; type = type.BaseType)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field is null) continue;
            field.SetValue(instance, value);
            return;
        }
        throw new MissingFieldException(instance.GetType().FullName, name);
    }
}
