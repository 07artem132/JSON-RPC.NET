using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WsRpcServer.Tests.Security;

/// <summary>
/// Будівник тестових сертифікатів (CA + клієнтські/серверні листки) через <see cref="CertificateRequest"/> —
/// без файлів і зовнішньої PKI. Дозволяє пінити валідатор/резолвер на реальних X509-структурах.
/// </summary>
internal static class TestCertificates
{
    private const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";

    /// <summary>Створює самопідписаний CA (з приватним ключем) для підпису листків.</summary>
    public static X509Certificate2 CreateCa(string subject = "CN=Test Root CA")
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
    }

    /// <summary>Створює листковий сертифікат, підписаний <paramref name="ca"/>, із EKU clientAuth.</summary>
    /// <param name="ca">Видавець (CA з приватним ключем).</param>
    /// <param name="subject">Subject DN листка.</param>
    /// <param name="sanUri">Опційний SAN URI (SPIFFE).</param>
    /// <param name="clientAuth">true → EKU clientAuth; false → serverAuth (для wrong-EKU тестів).</param>
    public static X509Certificate2 CreateLeaf(
        X509Certificate2 ca,
        string subject = "CN=billing-service",
        string? sanUri = null,
        bool clientAuth = true)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid(clientAuth ? ClientAuthOid : ServerAuthOid)], false));

        if (sanUri is not null)
        {
            var san = new SubjectAlternativeNameBuilder();
            san.AddUri(new Uri(sanUri));
            request.CertificateExtensions.Add(san.Build());
        }

        var serial = new byte[8];
        RandomNumberGenerator.Fill(serial);

        return request.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1), serial);
    }

    /// <summary>Самопідписаний листок (не chainується до жодного CA) для negative-тестів.</summary>
    public static X509Certificate2 CreateSelfSignedLeaf(string subject = "CN=rogue", string? sanUri = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ClientAuthOid)], false));

        if (sanUri is not null)
        {
            var san = new SubjectAlternativeNameBuilder();
            san.AddUri(new Uri(sanUri));
            request.CertificateExtensions.Add(san.Build());
        }

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    /// <summary>Самопідписаний серверний сертифікат із приватним ключем (для TlsServerOptions).</summary>
    public static X509Certificate2 CreateServerCertificate(string subject = "CN=localhost")
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ServerAuthOid)], false));

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }
}
