using System.Net.Sockets;
using System.Security.Authentication;
using Microsoft.Extensions.Logging;

namespace WsRpcServer.Logging;

/// <summary>
/// Source-generated логи для <see cref="Core.AbstractSecureJsonRpcServer"/>.
/// EventId-блок 1500–1549 (захищений транспорт).
/// </summary>
internal static partial class AbstractSecureJsonRpcServerLog
{
    [LoggerMessage(EventId = 1500, Level = LogLevel.Information,
        Message = "Захищений (TLS) JSON-RPC сервер ініціалізовано: clientCertRequired={ClientCertRequired}, protocols={Protocols}")]
    public static partial void SecureServerInitialized(ILogger logger, bool clientCertRequired, SslProtocols protocols);

    [LoggerMessage(EventId = 1501, Level = LogLevel.Error, Message = "Помилка захищеного WebSocket сервера: {Error}")]
    public static partial void ServerError(ILogger logger, SocketError error);
}
