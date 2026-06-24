# Спостережуваність + квота з'єднань — `WsRpcServerDiagnostics` та `MaxConcurrentConnections`

Метрики/трейси та квота одночасних з'єднань. Усе **additive**: інструментація інертна без підписників,
квота вимкнена за замовчуванням (`observability-and-resilience`, 2.7.0). Без нової NuGet-залежності
(`System.Diagnostics.DiagnosticSource` уже транзитивно присутній).

---

## `WsRpcServerDiagnostics`

Статичний клас зі спільними джерелами телеметрії з ім'ям `"WsRpcServer"`.

```csharp
public static class WsRpcServerDiagnostics
{
    public const string SourceName = "WsRpcServer";       // ім'я Meter і ActivitySource
    public const string ResultTagKey = "result";
    public static IReadOnlyCollection<string> AllowedTagKeys { get; }   // { "result" }
    public static ActivitySource ActivitySource { get; }
    // record-хелпери: ConnectionOpened/Closed/Rejected, Notification(bool), ParseFailure, AuthorizationDenied
}
```

### Інструменти `Meter` "WsRpcServer"

| Інструмент | Тип | Теги | Що міряє |
|---|---|---|---|
| `wsrpc.connections.active` | UpDownCounter`<long>` | — | Активні WebSocket-з'єднання (+1 на accept, −1 на teardown) |
| `wsrpc.connections.rejected` | Counter`<long>` | — | З'єднання, відхилені квотою `MaxConcurrentConnections` |
| `wsrpc.notifications` | Counter`<long>` | `result` ∈ {`queued`,`dropped`} | Сповіщення server→client за результатом постановки в чергу |
| `wsrpc.parse_failures` | Counter`<long>` | — | Невдалі розбори вхідного JSON (recovery-loop транспорту) |
| `wsrpc.authorization.denied` | Counter`<long>` | — | Відмови авторизації RPC (`[RpcAuthorize]` deny) |

`ActivitySource` "WsRpcServer" емітить span `wsrpc.connection` на життєвий цикл з'єднання (старт у
`OnConnected`, стоп у `OnDisconnected`).

### Приватність (інваріант)

Дзеркало SignalCli.NET: теги вимірів походять **лише** з фіксованого `AllowedTagKeys` (`result` —
енум-літерал), **ніколи** не несуть тіл повідомлень, номерів телефонів чи секретів ідентичності.
Регрес-guard `WsRpcServerDiagnosticsTests` через `MeterListener` пінить, що жоден захоплений тег-ключ не
виходить за allowlist. Підписка — стандартні `MeterListener` / `ActivityListener`:

```csharp
using var listener = new MeterListener();
listener.InstrumentPublished = (i, l) => { if (i.Meter.Name == "WsRpcServer") l.EnableMeasurementEvents(i); };
listener.SetMeasurementEventCallback<long>((i, v, tags, _) => /* експорт у ваш backend */);
listener.Start();
```

---

## Квота: `JsonRpcServerConfig.MaxConcurrentConnections`

Нове поле конфігу (див. повний reference у [`composition-and-config.md`](composition-and-config.md)):

| Властивість | Тип | Дефолт | Валідація | Опис |
|---|---|---|---|---|
| `MaxConcurrentConnections` | `int` | `0` | `[Range(0, int.MaxValue)]` | `0` = без ліміту. Коли `> 0` і активних з'єднань більше за поріг, сервер відхиляє нове з'єднання (розриває) ще до RPC-диспетчу + інкрементить `wsrpc.connections.rejected` |

Enforce'иться у `AbstractJsonRpcServer.OnConnected` (серверний TCP-accept seam, не споживацький
`OnWsConnected` сесії), тож не конфліктує з бізнес-логікою похідної сесії. Доповнює наявні anti-DoS
обмеження `MaxMessageSizeBytes` і `MaxConsecutiveParseFailures` (див. [`errors.md`](errors.md)). Guarded
реальним loopback-тестом `ConnectionQuotaTests`.
