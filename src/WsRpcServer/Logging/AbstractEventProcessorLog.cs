using Microsoft.Extensions.Logging;

namespace WsRpcServer.Logging;

/// <summary>
/// Source-generated логи для <see cref="Events.AbstractEventProcessor"/>.
/// EventId-блок 1400–1499.
/// </summary>
internal static partial class AbstractEventProcessorLog
{
    [LoggerMessage(EventId = 1400, Level = LogLevel.Information, Message = "Запуск обробника подій")]
    public static partial void Starting(ILogger logger);

    [LoggerMessage(EventId = 1401, Level = LogLevel.Information, Message = "Зупинка обробника подій")]
    public static partial void Stopping(ILogger logger);

    [LoggerMessage(EventId = 1402, Level = LogLevel.Information,
        Message = "Клієнт {ClientId} зареєстрований для отримання сповіщень про події")]
    public static partial void ClientRegistered(ILogger logger, Guid clientId);

    [LoggerMessage(EventId = 1403, Level = LogLevel.Information,
        Message = "Клієнт {ClientId} відписаний від сповіщень про події")]
    public static partial void ClientUnregistered(ILogger logger, Guid clientId);

    [LoggerMessage(EventId = 1404, Level = LogLevel.Error,
        Message = "Помилка відправки сповіщення {Method} клієнту {ClientId}")]
    public static partial void NotifyClientError(ILogger logger, Exception ex, string method, Guid clientId);

    [LoggerMessage(EventId = 1405, Level = LogLevel.Error,
        Message = "Помилка постановки сповіщення {Method} у чергу для клієнта {ClientId}")]
    public static partial void EnqueueNotificationError(ILogger logger, Exception ex, string method, Guid clientId);

    [LoggerMessage(EventId = 1406, Level = LogLevel.Warning,
        Message = "Клієнт {ClientId} вже зареєстрований — перезаписую обробник сповіщень")]
    public static partial void ClientAlreadyRegistered(ILogger logger, Guid clientId);

    [LoggerMessage(EventId = 1407, Level = LogLevel.Warning,
        Message = "Клієнт {ClientId} автоматично відписаний після {Failures} послідовних невдач доставки сповіщень")]
    public static partial void ClientAutoUnregistered(ILogger logger, Guid clientId, int failures);
}
