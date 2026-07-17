using System.Net.WebSockets;
using Microsoft.Extensions.Logging;

namespace WsRpcServer.Logging;

/// <summary>
/// Source-generated логи для <see cref="Sessions.AbstractJsonRpcSession"/>.
/// EventId-блок 1100–1199.
/// </summary>
internal static partial class AbstractJsonRpcSessionLog
{
    [LoggerMessage(EventId = 1100, Level = LogLevel.Debug,
        Message = "Пропуск сповіщення {Method} для закритої сесії {ClientId}")]
    public static partial void NotificationSkippedClosedSession(ILogger logger, string method, Guid clientId);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Warning,
        Message = "Канал сповіщень заповнений, пропуск сповіщення: {Method} для клієнта {ClientId}")]
    public static partial void NotificationChannelFull(ILogger logger, string method, Guid clientId);

    [LoggerMessage(EventId = 1102, Level = LogLevel.Debug,
        Message = "Додано сповіщення {Method} до черги для клієнта {ClientId}")]
    public static partial void NotificationQueued(ILogger logger, string method, Guid clientId);

    [LoggerMessage(EventId = 1103, Level = LogLevel.Debug,
        Message = "Запущено обробку сповіщень для клієнта {ClientId}")]
    public static partial void NotificationProcessingStarted(ILogger logger, Guid clientId);

    [LoggerMessage(EventId = 1104, Level = LogLevel.Warning,
        Message = "JSON-RPC відсутній, неможливо надіслати сповіщення {Method} для клієнта {ClientId}")]
    public static partial void NotificationNoJsonRpc(ILogger logger, string method, Guid clientId);

    [LoggerMessage(EventId = 1105, Level = LogLevel.Debug,
        Message = "Надіслано сповіщення {Method} клієнту {ClientId}")]
    public static partial void NotificationSent(ILogger logger, string method, Guid clientId);

    [LoggerMessage(EventId = 1106, Level = LogLevel.Debug,
        Message = "Обробку сповіщень скасовано для клієнта {ClientId}")]
    public static partial void NotificationProcessingCanceled(ILogger logger, Guid clientId);

    [LoggerMessage(EventId = 1107, Level = LogLevel.Error,
        Message = "Помилка надсилання сповіщення {Method} клієнту {ClientId}")]
    public static partial void NotificationSendError(ILogger logger, Exception ex, string method, Guid clientId);

    [LoggerMessage(EventId = 1108, Level = LogLevel.Warning,
        Message = "З'єднання втрачено для клієнта {ClientId}, зупиняємо обробку сповіщень")]
    public static partial void ConnectionLostStopping(ILogger logger, Guid clientId);

    [LoggerMessage(EventId = 1109, Level = LogLevel.Error,
        Message = "Помилка в циклі обробки сповіщень для клієнта {ClientId}")]
    public static partial void NotificationLoopError(ILogger logger, Exception ex, Guid clientId);

    [LoggerMessage(EventId = 1110, Level = LogLevel.Debug,
        Message = "Завершено обробку сповіщень для клієнта {ClientId}")]
    public static partial void NotificationProcessingFinished(ILogger logger, Guid clientId);

    [LoggerMessage(EventId = 1111, Level = LogLevel.Information,
        Message = "Закриття WebSocket з'єднання: {Status} - {Reason} для клієнта {ClientId}")]
    public static partial void ClosingConnection(ILogger logger, WebSocketCloseStatus status, string reason,
        Guid clientId);

    [LoggerMessage(EventId = 1112, Level = LogLevel.Error,
        Message = "Помилка закриття WebSocket з'єднання для клієнта {ClientId}")]
    public static partial void CloseError(ILogger logger, Exception ex, Guid clientId);

    [LoggerMessage(EventId = 1113, Level = LogLevel.Warning,
        Message = "Спроба надіслати бінарні дані після утилізації сесії {ClientId}")]
    public static partial void BinarySendAfterDispose(ILogger logger, Guid clientId);

    [LoggerMessage(EventId = 1114, Level = LogLevel.Debug,
        Message = "Надсилання бінарних даних розміром {Size} байтів для клієнта {ClientId}")]
    public static partial void SendingBinaryData(ILogger logger, int size, Guid clientId);

    [LoggerMessage(EventId = 1115, Level = LogLevel.Error,
        Message = "Помилка надсилання бінарних даних розміром {Size} байтів для клієнта {ClientId}")]
    public static partial void BinarySendError(ILogger logger, Exception ex, int size, Guid clientId);

    [LoggerMessage(EventId = 1116, Level = LogLevel.Debug,
        Message = "Отримано WebSocket ping для клієнта {ClientId}")]
    public static partial void PingReceived(ILogger logger, Guid clientId);

    [LoggerMessage(EventId = 1117, Level = LogLevel.Debug,
        Message = "Узгоджено WebSocket-субпротокол {Subprotocol} для клієнта {ClientId}")]
    public static partial void SubprotocolNegotiated(ILogger logger, string subprotocol, Guid clientId);

    [LoggerMessage(EventId = 1118, Level = LogLevel.Warning,
        Message = "Спроба надіслати текстові дані після утилізації сесії {ClientId}")]
    public static partial void TextSendAfterDispose(ILogger logger, Guid clientId);

    [LoggerMessage(EventId = 1119, Level = LogLevel.Debug,
        Message = "Надсилання текстових даних розміром {Size} байтів для клієнта {ClientId}")]
    public static partial void SendingTextData(ILogger logger, int size, Guid clientId);

    [LoggerMessage(EventId = 1120, Level = LogLevel.Error,
        Message = "Помилка надсилання текстових даних розміром {Size} байтів для клієнта {ClientId}")]
    public static partial void TextSendError(ILogger logger, Exception ex, int size, Guid clientId);
}
