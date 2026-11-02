using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using WenCun.Capture;

namespace WenCun.Tests;

public sealed class CaptureTests
{
    [Fact]
    public void LocalCertificateIsPersistentAndScoped()
    {
        using var certificate = LocalCaptureCertificate.CreateForTest();
        using var rsa = certificate.GetRSAPrivateKey();
        var cng = Assert.IsType<RSACng>(rsa);
        Assert.False(cng.Key.IsEphemeral);
        Assert.Contains("O=WenCun Local Capture", certificate.Subject);
        Assert.Equal("mp.weixin.qq.com", certificate.GetNameInfo(X509NameType.DnsName, false));
        Assert.True(certificate.NotAfter > DateTime.UtcNow.AddDays(300));
        cng.Key.Delete();
    }
}
