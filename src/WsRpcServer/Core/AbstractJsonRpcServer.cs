namespace WsRpcServer.Core;

using Microsoft.Extensions.Logging;
using NetCoreServer;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

/// <summary>
/// Абстрактний базовий клас для JSON-RPC WebSocket серверів.
/// Наслідується від WsServer з бібліотеки NetCoreServer для забезпечення низькорівневої роботи з WebSocket.
/// Реалізує патерн "Шаблонний метод", де базова логіка визначена тут, а специфічні деталі реалізуються у похідних класах.
/// </summary>
/// <remarks>
/// Використання NetCoreServer як базової бібліотеки дозволяє отримати високу продуктивність та низьку затримку, 
/// оскільки вона оптимізована для обробки тисяч з'єднань. Клас надає необхідну абстракцію для спрощення інтеграції 
/// з JSON-RPC, дозволяючи зосередитись на бізнес-логіці, а не на деталях транспортного рівня.
/// </remarks>
public abstract class AbstractJsonRpcServer(
    IPAddress address,
    int port,
    IServiceProvider serviceProvider,
    ILogger logger)
    : WsServer(address, port)
{
    /// <summary>
    /// Постачальник сервісів для створення екземплярів залежностей.
    /// Ключовий компонент для інтеграції з DI контейнером та забезпечення доступу до зареєстрованих сервісів.
    /// </summary>
    protected IServiceProvider ServiceProvider { get; } =
        serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

    /// <summary>
    /// Логер для реєстрації подій сервера.
    /// </summary>
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Створює нову WebSocket сесію для підключеного клієнта.
    /// Цей метод перевизначено для повернення спеціалізованого типу сесії для JSON-RPC.
    /// </summary>
    /// <returns>Новий екземпляр сесії.</returns>
    /// <remarks>
    /// NetCoreServer викликає цей метод при кожному новому з'єднанні.
    /// Використання фабричного методу дозволяє створювати різні типи сесій для різних реалізацій.
    /// </remarks>
    protected override WsSession CreateSession()
    {
        return CreateJsonRpcSession();
    }

    /// <summary>
    /// Створює нову спеціалізовану сесію JSON-RPC.
    /// Абстрактний метод, який має бути реалізований у похідних класах.
    /// </summary>
    /// <returns>Спеціалізований екземпляр WsSession для обробки JSON-RPC повідомлень.</returns>
    /// <remarks>
    /// Цей метод дозволяє кожній конкретній реалізації сервера визначати свій тип сесії,
    /// що забезпечує гнучкість у обробці різних типів клієнтських з'єднань.
    /// </remarks>
    protected abstract WsSession CreateJsonRpcSession();

    /// <summary>
    /// Обробляє помилки сокета, які виникають на рівні сервера.
    /// Перевизначає базову реалізацію з WsServer для забезпечення логування.
    /// </summary>
    /// <param name="error">Помилка сокета, що виникла.</param>
    /// <remarks>
    /// Викликається NetCoreServer автоматично при виникненні помилок сокета на серверному рівні.
    /// </remarks>
    protected override void OnError(SocketError error)
    {
        _logger.LogError("Помилка WebSocket сервера: {Error}", error);
        OnServerError(error);
    }

    /// <summary>
    /// Викликається при виникненні помилки сервера.
    /// Дозволяє похідним класам реагувати на помилки особливим чином.
    /// </summary>
    /// <param name="error">Помилка сокета, що виникла.</param>
    /// <remarks>
    /// Використовується для розширення обробки помилок без перевизначення базового методу OnError.
    /// Порожня реалізація за замовчуванням дозволяє похідним класам реагувати лише при необхідності.
    /// </remarks>
    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "C#-only project; renaming would break virtual-method overrides in consumer code.")]
    protected virtual void OnServerError(SocketError error)
    {
    }
}