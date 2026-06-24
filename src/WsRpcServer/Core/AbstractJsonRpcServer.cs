namespace WsRpcServer.Core;

using Microsoft.Extensions.Logging;
using NetCoreServer;
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using WsRpcServer.Diagnostics;
using WsRpcServer.Logging;

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
    /// Ліниво-резолвлена квота одночасних з'єднань (<c>0</c> = без ліміту). Резолвиться з
    /// <see cref="JsonRpcServerConfig"/> у DI; <c>0</c>, якщо конфіг недоступний (сервер без DI).
    /// </summary>
    private readonly Lazy<int> _maxConcurrentConnections = new(() =>
        (serviceProvider?.GetService(typeof(JsonRpcServerConfig)) as JsonRpcServerConfig)?.MaxConcurrentConnections ?? 0);

    /// <summary>
    /// Маркер «з'єднання зараховано» (для балансу гейджа) + span його життєвого циклу. Слабкі ключі —
    /// запис зникає разом із сесією без витоку.
    /// </summary>
    private readonly ConditionalWeakTable<TcpSession, ConnectionState> _connections = new();

    /// <summary>Стан зарахованого з'єднання: span (або <c>null</c>, якщо немає слухачів activity).</summary>
    private sealed class ConnectionState
    {
        public Activity? Activity { get; init; }
    }

    /// <summary>
    /// Обробляє нове TCP-з'єднання: enforce квоти <see cref="JsonRpcServerConfig.MaxConcurrentConnections"/>
    /// + облік активних з'єднань (метрика + span). Викликається NetCoreServer на рівні сервера.
    /// </summary>
    /// <param name="session">Нова сесія.</param>
    /// <remarks>
    /// Це серверний seam (не споживацький <c>OnWsConnected</c> сесії), тож інструментація/квота не
    /// конфліктують із бізнес-логікою похідної сесії. Відхилене за квотою з'єднання НЕ зараховується в
    /// гейдж і не доходить до RPC-диспетчу.
    /// </remarks>
    protected override void OnConnected(TcpSession session)
    {
        base.OnConnected(session);

        int max = _maxConcurrentConnections.Value;
        if (max > 0 && ConnectedSessions > max)
        {
            AbstractJsonRpcServerLog.ConnectionRejected(_logger, ConnectedSessions, max);
            WsRpcServerDiagnostics.ConnectionRejected();
            session.Disconnect();
            return;
        }

        WsRpcServerDiagnostics.ConnectionOpened();
        _connections.AddOrUpdate(session, new ConnectionState { Activity = WsRpcServerDiagnostics.StartConnectionActivity() });
    }

    /// <summary>
    /// Обробляє розрив TCP-з'єднання: знімає облік активних з'єднань + завершує span (лише для
    /// зарахованих з'єднань — відхилені за квотою ігноруються).
    /// </summary>
    /// <param name="session">Сесія, що від'єдналася.</param>
    protected override void OnDisconnected(TcpSession session)
    {
        if (_connections.TryGetValue(session, out var state))
        {
            _connections.Remove(session);
            WsRpcServerDiagnostics.ConnectionClosed();
            state.Activity?.Dispose();
        }

        base.OnDisconnected(session);
    }

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
        AbstractJsonRpcServerLog.ServerError(_logger, error);
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