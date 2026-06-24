using Microsoft.Extensions.Logging;

namespace WsRpcServer.Logging;

/// <summary>
/// Source-generated логи для <see cref="Security.CustomRootTrustValidator"/> та резолву ідентичності.
/// EventId-блок 1550–1599 (mTLS node identity).
/// </summary>
/// <remarks>
/// Приватність: логуємо лише SPKI-відбиток, Subject DN, SPIFFE-id та рішення — НІКОЛИ приватний ключ
/// чи повний PEM сертифіката.
/// </remarks>
internal static partial class NodeCertificateValidatorLog
{
    [LoggerMessage(EventId = 1550, Level = LogLevel.Warning,
        Message = "mTLS: клієнт не надав сертифіката — відмова.")]
    public static partial void NoCertificate(ILogger logger);

    [LoggerMessage(EventId = 1551, Level = LogLevel.Warning,
        Message = "mTLS: побудова ланцюга не вдалася (spki={Spki}, subject={Subject}, statuses={Statuses}) — відмова.")]
    public static partial void ChainBuildFailed(ILogger logger, string spki, string subject, string statuses);

    [LoggerMessage(EventId = 1552, Level = LogLevel.Warning,
        Message = "mTLS: SPKI поза allowlist'ом пінів (spki={Spki}, subject={Subject}) — відмова.")]
    public static partial void SpkiNotPinned(ILogger logger, string spki, string subject);

    [LoggerMessage(EventId = 1553, Level = LogLevel.Information,
        Message = "mTLS: сертифікат прийнято (spki={Spki}, subject={Subject}).")]
    public static partial void CertificateAccepted(ILogger logger, string spki, string subject);

    [LoggerMessage(EventId = 1554, Level = LogLevel.Information,
        Message = "mTLS: ідентичність вузла встановлено (name={Name}, spki={Spki}).")]
    public static partial void NodeIdentityResolved(ILogger logger, string name, string spki);
}
