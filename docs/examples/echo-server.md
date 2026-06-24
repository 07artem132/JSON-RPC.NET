# Приклад: end-to-end сервер

Збираємо всі точки розширення в робочий двобічний JSON-RPC сервер: RPC-методи (звичайні +
клієнт-залежні), серверні події з підписками, і власні сервер/сесія. Код віддзеркалює
`example/SimpleServer` у репозиторії — там він компілюється й запускається.

Шість шматків (по одному на шар) + `Program.cs`, що їх з'єднує через
[`AddJsonRpcCore<…>`](../api/composition-and-config.md).

---

## 1. RPC-сервіси (service-шар)

Звичайний сервіс — реалізує інтерфейс-нащадок `IRpcService`, резолвиться з DI:

```csharp
public interface ICalculatorService : IRpcService
{
    Task<int> Add(int a, int b);
    Task<int> Subtract(int a, int b);
}

public class CalculatorService(ILogger<CalculatorService> logger) : ICalculatorService
{
    public Task<int> Add(int a, int b) => Task.FromResult(a + b);
    public Task<int> Subtract(int a, int b) => Task.FromResult(a - b);
}
```

Клієнт-залежний сервіс — `IClientAwareRpcService`, отримує `Guid clientId` у конструктор
(деталі — [`services-and-registry.md`](../api/services-and-registry.md)):

```csharp
public interface IDemoEventsRpc : IClientAwareRpcService
{
    Task<int>  Subscribe(string topic, ServerEventType[] eventTypes, CancellationToken ct = default);
    Task<bool> Unsubscribe(int subscriptionId, CancellationToken ct = default);
}

public class DemoEventsRpcAdapter(
    ISubscriptionManager<ServerEventType, object> subs,
    ILogger<DemoEventsRpcAdapter> logger,
    Guid clientId) : IDemoEventsRpc
{
    public async Task<int> Subscribe(string topic, ServerEventType[] types, CancellationToken ct = default)
    {
        try { return await subs.Subscribe(clientId, topic, types, ct); }
        catch (Exception ex)
        {
            throw new RpcErrorException(JsonRpcErrorCode.InvocationError, "Subscription failed", ex);
        }
    }

    public Task<bool> Unsubscribe(int subscriptionId, CancellationToken ct = default) =>
        subs.Unsubscribe(clientId, subscriptionId, ct);
}
```

## 2. Реєстр сервісів

```csharp
public class DemoServiceRegistry(IServiceProvider sp, ILogger<DemoServiceRegistry> logger)
    : AbstractRpcServiceRegistry(sp, logger)
{
    protected override IEnumerable<string> GetAdditionalAssemblyPrefixes() => ["SimpleServer"];
}
```

## 3. Обробник подій ([events.md](../api/events.md))

```csharp
public record SystemStatusEvent(string Status, DateTime Timestamp);

public class DemoEventProcessor(ILogger<DemoEventProcessor> logger) : AbstractEventProcessor(logger)
{
    private Timer? _timer;

    public override Task StartAsync(CancellationToken ct)
    {
        _timer = new Timer(_ => PublishSystemStatus(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
        return base.StartAsync(ct);
    }

    public override Task StopAsync(CancellationToken ct)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return base.StopAsync(ct);
    }

    private void PublishSystemStatus()
    {
        var status = new SystemStatusEvent($"Active clients: {ClientHandlers.Count}", DateTime.UtcNow);
        foreach (var clientId in ClientHandlers.Keys)
            NotifyClient(clientId, "onSystemStatus", status);
    }
}
```

## 4. Менеджер підписок ([subscriptions.md](../api/subscriptions.md))

Реалізуй лише `*Core` (виконуються під `OperationLock`); НЕ клич публічний `Subscribe` зсередини `*Core`:

```csharp
public enum ServerEventType { SystemStatus, UserActivity }

public class DemoSubscriptionManager(ILogger<DemoSubscriptionManager> logger, DemoEventProcessor ep)
    : AbstractSubscriptionManager<ServerEventType, object>(logger, maxSubscriptionsPerClient: 10)
{
    private readonly Dictionary<Guid, HashSet<ServerEventType>> _map = new();

    protected override Task<int> SubscribeCore(
        Guid clientId, string topic, IReadOnlyCollection<ServerEventType> types, CancellationToken ct)
    {
        if (!_map.TryGetValue(clientId, out var set)) _map[clientId] = set = new();
        foreach (var t in types) set.Add(t);
        return Task.FromResult(999);
    }

    protected override Task<bool> UnsubscribeCore(Guid clientId, int subscriptionId, CancellationToken ct)
    {
        return Task.FromResult(_map.Remove(clientId));
    }

    protected override Task<bool> UpdateSubscriptionCore(
        Guid clientId, int subscriptionId, IReadOnlyCollection<ServerEventType> types, CancellationToken ct)
    {
        _ = SubscribeCore(clientId, string.Empty, types, ct);   // *Core напряму — вже під OperationLock
        return Task.FromResult(true);
    }

    public override List<Guid> GetClientsForEvent(object args, ServerEventType eventType) =>
        _map.Where(kv => kv.Value.Contains(eventType)).Select(kv => kv.Key).ToList();
}
```

## 5. Сервер і сесія ([server-and-session.md](../api/server-and-session.md))

```csharp
public class DemoJsonRpcServer(
    IPAddress address, int port, IServiceProvider sp, ILogger<DemoJsonRpcServer> logger)
    : AbstractJsonRpcServer(address, port, sp, logger)
{
    protected override WsSession CreateJsonRpcSession() =>
        ActivatorUtilities.CreateInstance<DemoJsonRpcSession>(ServiceProvider, this);
}
```

Сесія — серце з'єднання: у `OnWsConnected` будуєш `JsonRpc`, реєструєш сервіси й клієнта, запускаєш
фонову розсилку; у `OnWsDisconnected` — прибираєш:

```csharp
public sealed class DemoJsonRpcSession : AbstractJsonRpcSession
{
    private readonly IServiceProvider _sp;
    private readonly IRpcServiceRegistry _registry;
    private readonly IEventProcessor _events;
    private WebSocketMessageHandler? _handler;

    public DemoJsonRpcSession(
        WsServer server, ILogger<DemoJsonRpcSession> logger, IServiceProvider sp,
        IRpcServiceRegistry registry, IEventProcessor events, JsonRpcServerConfig config)
        : base(server, logger, config)
    {
        _sp = sp; _registry = registry; _events = events;
    }

    public override void OnWsConnected(HttpRequest request)
    {
        var formatter = new SystemTextJsonFormatter();
        formatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;

        _handler = new WebSocketMessageHandler(
            this, formatter, _sp.GetRequiredService<ILogger<WebSocketMessageHandler>>(), Config);

        JsonRpc = new JsonRpc(_handler, _handler);
        _registry.RegisterServices(JsonRpc, Id);          // чіпляє ICalculatorService, IDemoEventsRpc, …
        _events.RegisterClient(Id, SendNotificationAsync); // тепер NotifyClient доставлятиме цьому клієнту
        _ = ProcessNotificationsAsync(Cts.Token);          // фонова розсилка з bounded-каналу
        JsonRpc.StartListening();
    }

    public override void OnWsReceived(byte[] buffer, long offset, long size) =>
        _handler?.ProcessReceivedDataAsync(new ReadOnlyMemory<byte>(buffer, (int)offset, (int)size));

    public override void OnWsDisconnected()
    {
        _events.UnregisterClient(Id);
        Cts.Cancel();
        NotificationChannel.Writer.TryComplete();
    }
}
```

## 6. `Program.cs` — все разом

```csharp
var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));

// Бізнес-сервіси:
services.AddSingleton<ICalculatorService, CalculatorService>();

// Композиційний корінь — усі 5 core-сервісів + сервер одним викликом:
services.AddJsonRpcCore<
    DemoJsonRpcServer, DemoJsonRpcSession, DemoEventProcessor,
    DemoSubscriptionManager, DemoServiceRegistry,
    ServerEventType, object>(o =>
{
    o.Host = "0.0.0.0";
    o.Port = 9000;
});

var sp = services.BuildServiceProvider();

// Запуск — вручну: спершу процесор подій, потім сервер.
var events = sp.GetRequiredService<IEventProcessor>();
await events.StartAsync(CancellationToken.None);

var server = sp.GetRequiredService<DemoJsonRpcServer>();
Console.WriteLine($"RPC server on {server.Address}:{server.Port}");
server.Start();

Console.WriteLine("Press Enter to stop…");
Console.ReadLine();

server.Stop();
await events.StopAsync(CancellationToken.None);
```

---

## Що відбувається під час роботи

1. Клієнт підключається → `DemoJsonRpcServer.CreateJsonRpcSession()` створює `DemoJsonRpcSession`.
2. `OnWsConnected` будує `JsonRpc`, реєстр чіпляє `add`/`subtract`/`subscribe`/`unsubscribe`, клієнт
   стає у реєстр процесора подій.
3. Клієнт кличе `add(2,3)` → `CalculatorService.Add` → `5`.
4. Клієнт кличе `subscribe("sys", [SystemStatus])` → `DemoEventsRpcAdapter` → менеджер підписок.
5. Кожні 10с `DemoEventProcessor` шле `onSystemStatus`-notification усім зареєстрованим клієнтам через
   їхній bounded-канал (повільний клієнт втрачає найстаріші, не блокує сервер).
6. Клієнт відключається → `OnWsDisconnected` знімає його з процесора, скасовує `Cts`, закриває канал.

Native-AOT/trim-варіант цього ж сервера (opt-in catalog + binder) — [`../aot.md`](../aot.md).
