using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using WsRpcServer.Security;
using Xunit;

namespace WsRpcServer.Tests.Security;

/// <summary>
/// Guard для `mtls-node-identity`: <see cref="CustomRootTrustValidator"/> довіряє лише приватному CA,
/// вимагає EKU clientAuth, та (опційно) звіряє SPKI-пін. Невалідний сертифікат → false (рукостискання
/// обірветься, диспетч недосяжний).
/// </summary>
public sealed class CustomRootTrustValidatorTests
{
    // Тести використовують NoCheck для revocation — без CRL-інфраструктури в одиничному тесті.
    private static CustomRootTrustValidator Validator(
        X509Certificate2 ca, IReadOnlyCollection<string>? pins = null) =>
        new([ca], pins, X509RevocationMode.NoCheck, NullLogger.Instance);

    [Fact]
    public void Validate_NullCertificate_ReturnsFalse()
    {
        using var ca = TestCertificates.CreateCa();
        Assert.False(Validator(ca).Validate(null, null, SslPolicyErrors.None));
    }

    [Fact]
    public void Validate_CertChainingToTrustedRoot_WithClientAuth_ReturnsTrue()
    {
        using var ca = TestCertificates.CreateCa();
        using var leaf = TestCertificates.CreateLeaf(ca);

        Assert.True(Validator(ca).Validate(leaf, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void Validate_SelfSignedCert_ReturnsFalse()
    {
        using var ca = TestCertificates.CreateCa();
        using var rogue = TestCertificates.CreateSelfSignedLeaf();

        Assert.False(Validator(ca).Validate(rogue, null, SslPolicyErrors.None));
    }

    [Fact]
    public void Validate_CertFromUnknownCa_ReturnsFalse()
    {
        using var trustedCa = TestCertificates.CreateCa("CN=Trusted CA");
        using var otherCa = TestCertificates.CreateCa("CN=Other CA");
        using var leaf = TestCertificates.CreateLeaf(otherCa);

        Assert.False(Validator(trustedCa).Validate(leaf, null, SslPolicyErrors.None));
    }

    [Fact]
    public void Validate_WrongEku_ReturnsFalse()
    {
        using var ca = TestCertificates.CreateCa();
        using var leaf = TestCertificates.CreateLeaf(ca, clientAuth: false); // serverAuth EKU

        Assert.False(Validator(ca).Validate(leaf, null, SslPolicyErrors.None));
    }

    [Fact]
    public void Validate_SpkiInAllowlist_ReturnsTrue()
    {
        using var ca = TestCertificates.CreateCa();
        using var leaf = TestCertificates.CreateLeaf(ca);
        var spki = NodeIdentity.ComputeSpkiThumbprint(leaf);

        Assert.True(Validator(ca, [spki]).Validate(leaf, null, SslPolicyErrors.None));
    }

    [Fact]
    public void Validate_SpkiNotInAllowlist_ReturnsFalse()
    {
        using var ca = TestCertificates.CreateCa();
        using var leaf = TestCertificates.CreateLeaf(ca);

        // Пін, що НЕ збігається — CA-валідний сертифікат все одно відхиляється (defense-in-depth).
        Assert.False(Validator(ca, ["DEADBEEF"]).Validate(leaf, null, SslPolicyErrors.None));
    }
}
