# Сервер, сесія, транспорт — `AbstractJsonRpcServer`, `AbstractJsonRpcSession`, `IJsonRpcSession`, `WebSocketMessageHandler`

Транспортний + сесійний шари (1 і 3 із п'яти). Сервер приймає WS-з'єднання й створює сесію на кожне;
сесія володіє екземпляром `JsonRpc` (StreamJsonRpc) і життєвим циклом з'єднання.

---

## `AbstractJsonRpcServer`

```csharp
public abstract class AbstractJsonRpcServer(
    IPAddress address, int port, IServiceProvider serviceProvider, ILogger logger) : WsServer
{
    protected IServiceProvider ServiceProvider { get; }
    protected override WsSession CreateSession();                 // sealed-шлях: делегує нижче
    protected abstract WsSession CreateJsonRpcSession();          // ← перевизнач це
    protected override void OnError(SocketError error);           // логує + кличе OnServerError
    protected virtual void OnServerError(SocketError error);      // hook для derived
}
```

Нащадок NetCoreServer `WsServer`. Перевизнач **лише** `CreateJsonRpcSession()` — поверни нову сесію,
сконструйовану через DI:

```csharp
public class DemoJsonRpcServer(
    IPAddress address, int port, IServiceProvider serviceProvider, ILogger<DemoJsonRpcServer> logger)
    : AbstractJsonRpcServer(address, port, serviceProvider, logger)
{
    protected override WsSession CreateJsonRpcSession() =>
        ActivatorUtilities.CreateInstance<DemoJsonRpcSession>(ServiceProvider, this);
}
```

> **Конструктор фіксований contract'ом `AddJsonRpcCore<…>`.** Композиційний корінь будує сервер як
> `ActivatorUtilities.CreateInstance<TServer>(sp, ipAddress, config.Port, sp, logger)` — тому
> сигнатура `(IPAddress, int, IServiceProvider, ILogger<TServer>)` обов'язкова
> ([`composition-and-config.md`](composition-and-config.md)). `Start()`/`Stop()`/`Address`/`Port` —
> від базового `WsServer`.

---

## `AbstractJsonRpcSession`

Життєвий цикл одного клієнтського з'єднання. Володіє `JsonRpc`, bounded-каналом сповіщень і власним
`CancellationTokenSource`.

```csharp
public abstract class AbstractJsonRpcSession(WsServer server, ILogger logger, JsonRpcServerConfig config)
    : WsSession
{
    protected ILogger Logger { get; }
    protected CancellationTokenSource Cts { get; }
    protected Channel<RpcNotification> NotificationChannel { get; }   // CreateBounded, DropOldest
    protected JsonRpcServerConfig Config { get; }
    protected JsonRpc? JsonRpc { get; set; }                           // встанови у OnWsConnected
    protected Task? NotificationProcessingTask { get; set; }

    public virtual Task SendNotificationAsync(string method, params object[] args);  // лише пише в канал
    public virtual Task SendBinaryDataAsync(ReadOnlyMemory<byte> data);
    public virtual void Close(WebSocketCloseStatus status, string reason);
    protected async Task ProcessNotificationsAsync(CancellationToken cancellationToken);
    public override void OnWsPing(byte[] buffer, long offset, long size);  // override, НЕ new (L3)
    protected override void Dispose(bool disposingManagedResources);
}
```

Що перевизначати в нащадку (див. `DemoJsonRpcSession` в `example/SimpleServer`):

- **`OnWsConnected(HttpRequest)`** — створи `WebSocketMessageHandler`, побудуй `JsonRpc`, зареєструй
  сервіси через `IRpcServiceRegistry.RegisterServices(JsonRpc, Id)`, зареєструй клієнта в
  `IEventProcessor.RegisterClient(Id, SendNotificationAsync)`, запусти
  `ProcessNotificationsAsync(Cts.Token)` у фоні, виклич `JsonRpc.StartListening()`.
- **`OnWsDisconnected()`** — `IEventProcessor.UnregisterClient(Id)`, `Cts.Cancel()`,
  `NotificationChannel.Writer.TryComplete()`.

Ключові інваріанти:

- **`SendNotificationAsync` миттєвий** — лише пише `RpcNotification` у bounded-канал і повертає
  завершений `Task` (XMLDoc L4). Реальна доставка — у фоновому `ProcessNotificationsAsync`. Канал
  `DropOldest`: повільний клієнт втрачає найстаріші сповіщення, не блокує сервер.
- **Disposal спершу скасовує, потім звільняє** (rule #3, H4, `security-hardening` 1.2.0): `Dispose`
  робить `Cts.Cancel()`, дренує `NotificationProcessingTask` (`.Wait(5s)`), і лише тоді звільняє Cts.
  Не звільняй Cts до скасування — осиротиш фонову задачу.
- **`OnWsPing` — `override`, не `new`** (L3, `low-severity-polish` 2.2.0): базовий
  `WsSession.OnWsPing` у поточному NetCoreServer віртуальний, тож `new` дав би фреймворку тихо обійти
  наш handler. Пінить `WsSessionOnWsPingGuardTests`.

---

## `IJsonRpcSession`

Публічний контракт сесії (те, що видно RPC-сервісам і event-processor'у):

```csharp
public interface IJsonRpcSession
{
    Guid Id { get; }                                                  // глобально унікальний id клієнта
    Task SendNotificationAsync(string method, params object[] args);
    Task SendBinaryDataAsync(ReadOnlyMemory<byte> data);
    void Close(WebSocketCloseStatus status, string reason);
}
```

`Id` (Guid) — наскрізний ідентифікатор клієнта: ним оперують `IEventProcessor.RegisterClient`,
`ISubscriptionManager.Subscribe`, і `IRpcServiceRegistry.RegisterServices(jsonRpc, clientId)`.

---

## `WebSocketMessageHandler`

```csharp
public sealed class WebSocketMessageHandler : MessageHandlerBase, IJsonRpcMessageBufferManager
{
    public WebSocketMessageHandler(
        IJsonRpcSession session, IJsonRpcMessageFormatter formatter,
        ILogger<WebSocketMessageHandler> logger, JsonRpcServerConfig config);

    public override bool CanRead => !_disposed;     // M9: віддзеркалює disposal, не hardcoded true
    public override bool CanWrite => !_disposed;
    public ValueTask<FlushResult> ProcessReceivedDataAsync(ReadOnlyMemory<byte> buffer);
}
```

`Stream`-адаптер (через `MessageHandlerBase`), що згодовує WS-фрейми у StreamJsonRpc. Зазвичай ти лише
**конструюєш** його в `OnWsConnected` і передаєш у `new JsonRpc(handler, handler)` — перевизначати
нічого. Подавай вхідні фрейми через `ProcessReceivedDataAsync` із `OnWsReceived`.

Вшиті захисти:

- **Bounded parse-recovery** (rule #5, H2): після `Config.MaxConsecutiveParseFailures` поспіль
  невдалих розборів з'єднання закривається `ProtocolError` — а не крутиться в нескінченному
  recovery-циклі (CPU-burn DoS). Лічильник скидається після успішного розбору. Деталі — [`errors.md`](errors.md).
- **`ProcessReceivedDataAsync` після `Dispose` кидає `ObjectDisposedException`** (R2-M2) — симетрично
  до шляху запису, замість витоку внутрішнього `InvalidOperationException` від завершеного `PipeWriter`.
  Пінить `WebSocketMessageHandlerDisposalGuardTests` (Roslyn-скан: будь-який метод, що пише в `_writer`,
  мусить перевіряти `_disposed`).
- **`CanRead`/`CanWrite` віддзеркалюють `_disposed`** (M9) — щоб StreamJsonRpc не кликав у звільнений handler.
