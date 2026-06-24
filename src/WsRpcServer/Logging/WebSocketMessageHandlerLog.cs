using Microsoft.Extensions.Logging;

namespace WsRpcServer.Logging;

/// <summary>
/// Source-generated логи для <see cref="Transport.WebSocketMessageHandler"/>.
/// EventId-блок 1200–1299.
/// </summary>
internal static partial class WebSocketMessageHandlerLog
{
    [LoggerMessage(EventId = 1200, Level = LogLevel.Debug,
        Message = "Створено WebSocketMessageHandler для сесії {SessionId} з порогом {Threshold} байт")]
    public static partial void HandlerCreated(ILogger logger, Guid sessionId, int threshold);

    [LoggerMessage(EventId = 1201, Level = LogLevel.Debug,
        Message = "Обробка отриманих даних розміром {Size} байт для сесії {SessionId}")]
    public static partial void ProcessingReceivedData(ILogger logger, int size, Guid sessionId);

    [LoggerMessage(EventId = 1202, Level = LogLevel.Error,
        Message = "Помилка обробки даних WebSocket для сесії {SessionId}")]
    public static partial void ProcessDataError(ILogger logger, Exception ex, Guid sessionId);

    [LoggerMessage(EventId = 1203, Level = LogLevel.Debug,
        Message = "Завершено десеріалізацію повідомлення для сесії {SessionId}")]
    public static partial void DeserializationComplete(ILogger logger, Guid sessionId);

    [LoggerMessage(EventId = 1204, Level = LogLevel.Error,
        Message = "Помилка завершення десеріалізації повідомлення для сесії {SessionId}")]
    public static partial void DeserializationCompleteError(ILogger logger, Exception ex, Guid sessionId);

    [LoggerMessage(EventId = 1205, Level = LogLevel.Debug,
        Message = "Читання скасовано або завершено для сесії {SessionId}")]
    public static partial void ReadCanceledOrCompleted(ILogger logger, Guid sessionId);

    [LoggerMessage(EventId = 1206, Level = LogLevel.Debug,
        Message = "Спроба десеріалізації JSON-RPC повідомлення для сесії {SessionId}")]
    public static partial void AttemptingDeserialize(ILogger logger, Guid sessionId);

    [LoggerMessage(EventId = 1207, Level = LogLevel.Debug,
        Message = "Успішно десеріалізовано повідомлення типу {MessageType} для сесії {SessionId}")]
    public static partial void DeserializeOk(ILogger logger, string messageType, Guid sessionId);

    [LoggerMessage(EventId = 1208, Level = LogLevel.Warning,
        Message = "Помилка розбору JSON-RPC повідомлення для сесії {SessionId} (підряд {Count}/{Max}). Позиція: {Position}")]
    public static partial void ParseError(ILogger logger, Exception ex, Guid sessionId, int count, int max,
        long? position);

    [LoggerMessage(EventId = 1209, Level = LogLevel.Error,
        Message = "Неочікувана помилка під час десеріалізації повідомлення для сесії {SessionId}")]
    public static partial void UnexpectedDeserializeError(ILogger logger, Exception ex, Guid sessionId);

    [LoggerMessage(EventId = 1210, Level = LogLevel.Warning,
        Message = "Перевищено ліміт послідовних помилок розбору ({Max}) для сесії {SessionId} — закриваю з'єднання")]
    public static partial void ParseLimitExceeded(ILogger logger, int max, Guid sessionId);

    [LoggerMessage(EventId = 1211, Level = LogLevel.Debug,
        Message = "Операцію читання скасовано для сесії {SessionId}")]
    public static partial void ReadCanceled(ILogger logger, Guid sessionId);

    [LoggerMessage(EventId = 1212, Level = LogLevel.Error,
        Message = "Неочікувана помилка читання даних WebSocket для сесії {SessionId}")]
    public static partial void ReadError(ILogger logger, Exception ex, Guid sessionId);

    [LoggerMessage(EventId = 1213, Level = LogLevel.Warning,
        Message = "Спроба запису в утилізований WebSocketMessageHandler для сесії {SessionId}")]
    public static partial void WriteAfterDispose(ILogger logger, Guid sessionId);

    [LoggerMessage(EventId = 1214, Level = LogLevel.Debug,
        Message = "Серіалізація та надсилання JSON-RPC повідомлення для сесії {SessionId}")]
    public static partial void SerializingMessage(ILogger logger, Guid sessionId);

    [LoggerMessage(EventId = 1215, Level = LogLevel.Debug,
        Message = "Надіслано JSON-RPC повідомлення розміром {Size} байт для сесії {SessionId}")]
    public static partial void MessageSent(ILogger logger, int size, Guid sessionId);

    [LoggerMessage(EventId = 1216, Level = LogLevel.Error,
        Message = "Помилка надсилання JSON-RPC повідомлення для сесії {SessionId}")]
    public static partial void SendMessageError(ILogger logger, Exception ex, Guid sessionId);

    [LoggerMessage(EventId = 1217, Level = LogLevel.Debug,
        Message = "Утилізація WebSocketMessageHandler для сесії {SessionId}")]
    public static partial void Disposing(ILogger logger, Guid sessionId);

    [LoggerMessage(EventId = 1218, Level = LogLevel.Warning,
        Message = "Спроба обробки отриманих даних у вже утилізованому WebSocketMessageHandler для сесії {SessionId}")]
    public static partial void ReceiveAfterDispose(ILogger logger, Guid sessionId);
}
