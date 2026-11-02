using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WenCun.Capture;

/// <summary>
/// 一次安装、长期复用的本地根证书。
/// Windows 对“把证书加入根存储”没有静默 API，首次安装/移除会弹一次系统确认；
/// 之后每次采集直接复用，不再弹窗。
/// </summary>
public sealed class LocalCaptureCertificate : IDisposable
{
    public const string SubjectName = "CN=mp.weixin.qq.com, O=WenCun Local Capture";
    private const string LegacySubjectName = "CN=mp.weixin.qq.com, O=WenCun Temporary Capture";

    private LocalCaptureCertificate(X509Certificate2 certificate) => Certificate = certificate;

    public X509Certificate2 Certificate { get; }

    public static LocalCaptureCertificate LoadOrCreate()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        var existing = store.Certificates
            .Where(cert => cert.Subject.Equals(SubjectName, StringComparison.Ordinal))
            .Where(cert => cert.HasPrivateKey && cert.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(30))
            .OrderByDescending(cert => cert.NotAfter)
            .FirstOrDefault();
        if (existing is not null) return new LocalCaptureCertificate(existing);
        var created = Create();
        store.Add(created);
        return new LocalCaptureCertificate(created);
    }

    internal static X509Certificate2 CreateForTest() => Create();

    private static X509Certificate2 Create()
    {
        var keyName = "WenCun-Capture-" + Guid.NewGuid().ToString("N");
        var options = new CngKeyCreationParameters
        {
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
            KeyUsage = CngKeyUsages.Signing | CngKeyUsages.Decryption,
            ExportPolicy = CngExportPolicies.None,
            KeyCreationOptions = CngKeyCreationOptions.None
        };
        options.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(2048), CngPropertyOptions.None));
        var key = CngKey.Create(CngAlgorithm.Rsa, keyName, options);
        using var rsa = new RSACng(key);
        var request = new CertificateRequest(SubjectName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("mp.weixin.qq.com");
        request.CertificateExtensions.Add(names.Build(true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
    }

    public bool IsTrusted()
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        foreach (var installed in store.Certificates)
        {
            using (installed)
                if (installed.Thumbprint == Certificate.Thumbprint) return true;
        }
        return false;
    }

    /// <summary>首次调用会弹一次 Windows 安全警告；用户点“是”后长期有效。</summary>
    public void EnsureTrusted()
    {
        if (IsTrusted()) return;
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite | OpenFlags.OpenExistingOnly);
        store.Add(Certificate);
        if (!IsTrusted())
            throw new IOException("本地证书没有被信任；请在弹出的 Windows 安全警告中点击『是』。");
    }

    public static void RemovePersistent()
    {
        using var my = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        my.Open(OpenFlags.ReadWrite);
        using var root = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        root.Open(OpenFlags.ReadWrite);
        foreach (var cert in my.Certificates.Where(IsOwned).ToArray())
        {
            using (cert)
            {
                root.Remove(cert);
                my.Remove(cert);
            }
        }
    }

    private static bool IsOwned(X509Certificate2 certificate) =>
        certificate.Subject.Equals(SubjectName, StringComparison.Ordinal) ||
        certificate.Subject.Equals(LegacySubjectName, StringComparison.Ordinal);

    public void Dispose() => Certificate.Dispose();
}
