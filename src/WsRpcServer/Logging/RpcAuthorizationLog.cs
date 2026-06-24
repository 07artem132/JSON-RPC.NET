using Microsoft.Extensions.Logging;

namespace WsRpcServer.Logging;

/// <summary>
/// Source-generated логи для авторизації RPC-викликів.
/// EventId-блок 1600–1699 (authorization).
/// </summary>
/// <remarks>
/// Приватність: логуємо ім'я методу та ім'я ідентичності (SPIFFE-id / SPKI) — не вміст запиту.
/// </remarks>
internal static partial class RpcAuthorizationLog
{
    [LoggerMessage(EventId = 1600, Level = LogLevel.Warning,
        Message = "Авторизацію відмовлено: метод={Method}, ідентичність={Identity}.")]
    public static partial void AuthorizationDenied(ILogger logger, string method, string identity);
}
