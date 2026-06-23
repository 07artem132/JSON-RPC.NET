using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace WsRpcServer.Logging;

/// <summary>
/// Source-generated логи для <see cref="Core.AbstractJsonRpcServer"/>.
/// EventId-блок 1000–1099.
/// </summary>
internal static partial class AbstractJsonRpcServerLog
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Error, Message = "Помилка WebSocket сервера: {Error}")]
    public static partial void ServerError(ILogger logger, SocketError error);
}
