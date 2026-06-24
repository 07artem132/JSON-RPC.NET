using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using WsRpcServer.Logging;

namespace WsRpcServer.Security;

/// <summary>
/// Типовий <see cref="INodeCertificateValidator"/>: будує власний <see cref="X509Chain"/> з
/// <see cref="X509ChainTrustMode.CustomRootTrust"/> + приватним CA, вимагає EKU <c>clientAuth</c>,
/// відхиляє на будь-якій помилці ланцюга та (опційно) звіряє SPKI-пін.
/// </summary>
/// <remarks>
/// Чому власний ланцюг, а не машинне сховище: довіра має бути обмежена приватним CA розгортання
/// (<see cref="X509ChainTrustMode.CustomRootTrust"/> + <see cref="X509ChainPolicy.CustomTrustStore"/>),
/// інакше будь-який публічно довірений CA міг би видати «валідний» клієнтський сертифікат.
/// Режим відкликання за замовчуванням <see cref="X509RevocationMode.Offline"/> — щоб уникнути
/// DoS під час рукостискання на повільному OCSP/CRL-респондері.
/// </remarks>
public sealed class CustomRootTrustValidator : INodeCertificateValidator
{
    /// <summary>OID розширеного використання ключа <c>clientAuth</c> (1.3.6.1.5.5.7.3.2).</summary>
    private const string ClientAuthEku = "1.3.6.1.5.5.7.3.2";

    private readonly X509Certificate2Collection _trustedRoots;
    private readonly HashSet<string> _spkiPins;
    private readonly X509RevocationMode _revocationMode;
    private readonly ILogger _logger;

    /// <summary>
    /// Створює валідатор.
    /// </summary>
    /// <param name="trustedRoots">Довірені корені (приватний CA). Не може бути порожнім для робочого mTLS.</param>
    /// <param name="spkiPins">Опційний allowlist SPKI-SHA-256 пінів (hex). Порожній — пін вимкнено.</param>
    /// <param name="revocationMode">Режим перевірки відкликання.</param>
    /// <param name="logger">Логер.</param>
    public CustomRootTrustValidator(
        IReadOnlyCollection<X509Certificate2> trustedRoots,
        IReadOnlyCollection<string>? spkiPins,
        X509RevocationMode revocationMode,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(trustedRoots);
        ArgumentNullException.ThrowIfNull(logger);

        _trustedRoots = [.. trustedRoots];
        _spkiPins = new HashSet<string>(spkiPins ?? [], StringComparer.OrdinalIgnoreCase);
        _revocationMode = revocationMode;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool Validate(X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
    {
        if (certificate is null)
        {
            NodeCertificateValidatorLog.NoCertificate(_logger);
            return false;
        }

        var spki = NodeIdentity.ComputeSpkiThumbprint(certificate);

        // Будуємо ВЛАСНИЙ ланцюг: довіряємо лише приватному CA, вимагаємо EKU clientAuth.
        using var builtChain = new X509Chain();
        builtChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        builtChain.ChainPolicy.CustomTrustStore.AddRange(_trustedRoots);
        builtChain.ChainPolicy.RevocationMode = _revocationMode;
        builtChain.ChainPolicy.ApplicationPolicy.Add(new Oid(ClientAuthEku));

        if (!builtChain.Build(certificate))
        {
            var statuses = string.Join(
                ", ",
                builtChain.ChainStatus.Select(s => s.Status.ToString()).Distinct());
            NodeCertificateValidatorLog.ChainBuildFailed(_logger, spki, certificate.Subject, statuses);
            return false;
        }

        // SPKI-пін (defense-in-depth поверх довіри до CA).
        if (_spkiPins.Count > 0 && !_spkiPins.Contains(spki))
        {
            NodeCertificateValidatorLog.SpkiNotPinned(_logger, spki, certificate.Subject);
            return false;
        }

        NodeCertificateValidatorLog.CertificateAccepted(_logger, spki, certificate.Subject);
        return true;
    }
}
