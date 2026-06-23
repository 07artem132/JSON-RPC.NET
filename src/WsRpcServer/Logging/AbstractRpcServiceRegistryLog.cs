using Microsoft.Extensions.Logging;

namespace WsRpcServer.Logging;

/// <summary>
/// Source-generated логи для <see cref="Services.AbstractRpcServiceRegistry"/>.
/// EventId-блок 1300–1399.
/// </summary>
internal static partial class AbstractRpcServiceRegistryLog
{
    [LoggerMessage(EventId = 1300, Level = LogLevel.Debug,
        Message = "Реєстрація RPC-сервісів для клієнта {ClientId}")]
    public static partial void RegisteringServices(ILogger logger, Guid clientId);

    [LoggerMessage(EventId = 1301, Level = LogLevel.Debug, Message = "Зареєстровано RPC-сервіс: {Type}")]
    public static partial void ServiceRegistered(ILogger logger, string type);

    [LoggerMessage(EventId = 1302, Level = LogLevel.Error, Message = "Помилка при реєстрації RPC-сервісу {Type}")]
    public static partial void ServiceRegisterError(ILogger logger, Exception ex, string type);

    [LoggerMessage(EventId = 1303, Level = LogLevel.Debug,
        Message = "Зареєстровано клієнт-специфічний RPC-сервіс: {Type}")]
    public static partial void ClientAwareServiceRegistered(ILogger logger, string type);

    [LoggerMessage(EventId = 1304, Level = LogLevel.Error,
        Message = "Помилка при реєстрації клієнт-специфічного сервісу {Type}")]
    public static partial void ClientAwareServiceRegisterError(ILogger logger, Exception ex, string type);

    [LoggerMessage(EventId = 1305, Level = LogLevel.Information,
        Message = "Зареєстровано {Count} RPC-сервісів для клієнта {ClientId}")]
    public static partial void ServicesRegistered(ILogger logger, int count, Guid clientId);

    [LoggerMessage(EventId = 1306, Level = LogLevel.Warning,
        Message = "Знайдено {Count} реалізацій інтерфейсу {Interface}; використано {Chosen}, решту проігноровано. Уточніть реєстрацію через DI.")]
    public static partial void MultipleImplementations(ILogger logger, int count, string @interface, string chosen);

    [LoggerMessage(EventId = 1307, Level = LogLevel.Error, Message = "Помилка при скануванні типів RPC-сервісів")]
    public static partial void ScanError(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 1308, Level = LogLevel.Debug,
        Message = "Використано source-генерований каталог RPC-сервісів ({Count} сервісів) — без рефлексії")]
    public static partial void CatalogUsed(ILogger logger, int count);
}
