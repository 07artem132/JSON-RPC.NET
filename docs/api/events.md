# Доставка подій — `IEventProcessor`, `AbstractEventProcessor`, `RpcNotification`

Event-шар: fan-out сповіщень server→client. Процесор тримає реєстр per-client handler'ів і доставляє
кожному зареєстрованому клієнту нотифікації за патерном «fire-and-forget» з лічильником збоїв.

---

## `IEventProcessor`

```csharp
public interface IEventProcessor
{
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    void RegisterClient(Guid clientId, Func<string, object[], Task> notificationHandler);
    void UnregisterClient(Guid clientId);
}
```

| Член | Призначення |
|---|---|
| `StartAsync` / `StopAsync` | Життєвий цикл процесора (виклич вручну біля старту/зупинки сервера) |
| `RegisterClient` | Реєструє клієнта + його handler доставки (зазвичай `session.SendNotificationAsync`) |
| `UnregisterClient` | Знімає клієнта (виклич у `OnWsDisconnected`) |

`AddJsonRpcCore<…>` реєструє твій `TEventProcessor` і як `IEventProcessor`, і як конкретний тип
(«один екземпляр — дві ролі»), тож derived-API (твої `Publish*`) доступний через конкретний тип.

---

## `AbstractEventProcessor`

```csharp
public abstract class AbstractEventProcessor(ILogger logger, int maxConsecutiveNotificationFailures = 5)
    : IEventProcessor, IDisposable
{
    protected ILogger Logger { get; }
    protected ConcurrentDictionary<Guid, Func<string, object[], Task>> ClientHandlers { get; }
    protected CancellationTokenSource Cts { get; }
    protected ConcurrentBag<IDisposable> Subscriptions { get; }          // L2: concurrent — реєструй з будь-якого потоку
    protected bool IsDisposed { get; set; }

    public virtual Task StartAsync(CancellationToken cancellationToken);
    public virtual Task StopAsync(CancellationToken cancellationToken);
    public virtual void RegisterClient(Guid clientId, Func<string, object[], Task> handler);  // L1: TryAdd + Warning на дубль
    public virtual void UnregisterClient(Guid clientId);
    protected virtual void NotifyClient(Guid clientId, string method, object eventArgs);       // ← клич це для розсилки
    protected virtual void HandleClientFailure(Guid clientId);                                 // hook для derived (метрики/back-off)
    public virtual void Dispose();
}
```

Як писати нащадок (див. `DemoEventProcessor`): тримай джерело подій (таймер, зовнішній стрім), і на
кожну подію проходь `ClientHandlers.Keys`, викликаючи `NotifyClient(clientId, method, payload)`:

```csharp
public class DemoEventProcessor(ILogger<DemoEventProcessor> logger) : AbstractEventProcessor(logger)
{
    private readonly Timer _timer; // → PublishSystemStatus кожні 10с

    private void PublishSystemStatus()
    {
        var status = new SystemStatusEvent($"Active clients: {ClientHandlers.Count}", DateTime.UtcNow);
        foreach (var clientId in ClientHandlers.Keys)
            NotifyClient(clientId, "onSystemStatus", status);   // "onSystemStatus" — JSON-RPC notification method
    }
}
```

Клієнт отримає JSON-RPC notification з ім'ям методу (`"onSystemStatus"`) і payload'ом як параметром.

Інваріанти (rules #1/#3, M1/L1/L2, `low-severity-polish` 2.2.0):

- **Auto-unregister проблемного клієнта** (M1): доставка — fire-and-forget через `Task.ContinueWith`.
  Збої маршрутизуються через лічильник `_consecutiveFailures`: на
  `maxConsecutiveNotificationFailures`-й (ctor-параметр, дефолт **5**) збій поспіль клієнт
  авто-`UnregisterClient`'иться + Warning. Успішна доставка скидає лічильник. `HandleClientFailure`
  кличеться на **кожен** збій — він **додатковий** до авто-зняття, не заміна.
- **`RegisterClient` — `TryAdd` + Warning на дубль** (L1), не тихий overwrite.
- **`Subscriptions` — `ConcurrentBag<IDisposable>`** (L2): реєструй disposable-и з кількох потоків.
- **Disposal спершу скасовує** (rule #3, H4): `Dispose` → `Cts.Cancel()` → дренаж → `Cts.Dispose()`.

---

## `RpcNotification`

```csharp
public record RpcNotification(string Method, object[] Arguments);
```

DTO одного відкладеного сповіщення. Сесія кладе його у свій bounded `Channel<RpcNotification>` у
`SendNotificationAsync`, а фоновий `ProcessNotificationsAsync` дренує канал і шле реальні JSON-RPC
notification'и. `record` — для value-рівності + `with`. Здебільшого ти його не конструюєш напряму —
`NotifyClient` / `SendNotificationAsync(method, args)` роблять це за тебе.
