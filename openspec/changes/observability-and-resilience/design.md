# Design — observability-and-resilience

## Instrumentation seams (framework-owned, не конфліктують зі споживачем)

NetCoreServer-ієрархія дає кілька `protected virtual` точок на РІВНІ СЕРВЕРА, які споживач зазвичай не
чіпає (він перевизначає `OnWsConnected`/`OnWsReceived` на рівні СЕСІЇ):

- `TcpServer.OnConnected(TcpSession)` / `OnDisconnected(TcpSession)` — TCP-accept/teardown; ідеальні для
  гейджа активних з'єднань + квоти. `TcpServer.ConnectedSessions` дає поточну кількість.
- `SendNotificationAsync` (база сесії, наш код) — `queued` vs `dropped` (канал повний).
- `WebSocketMessageHandler` recovery-loop (наш код) — `parse_failures`.
- `RpcAuthorizationEnforcer.Enforce` (наш код, 2.6.0) — `authorization.denied`.

## `WsRpcServerDiagnostics`

Статичний клас зі спільними `Meter`/`ActivitySource`:

```
Meter "WsRpcServer":
  wsrpc.connections.active    UpDownCounter<long>   // +1 accept, -1 teardown (тільки для зарахованих)
  wsrpc.connections.rejected  Counter<long>         // квота
  wsrpc.notifications         Counter<long>  tag result=queued|dropped
  wsrpc.parse_failures        Counter<long>
  wsrpc.authorization.denied  Counter<long>
ActivitySource "WsRpcServer":
  span "wsrpc.connection"     // старт у OnConnected (зарахованого), стоп у OnDisconnected
```

Безпечний набір тег-ключів зафіксовано константою `AllowedTagKeys = { "result" }` — privacy-guard
звіряє з нею захоплені `MeterListener`-ом виміри.

## Connection lifecycle у `AbstractJsonRpcServer`

`OnConnected(TcpSession)` (після `base`):
- зчитати `MaxConcurrentConnections` з `JsonRpcServerConfig` (резолв із `ServiceProvider` один раз, кеш);
- якщо `Max > 0` і `ConnectedSessions > Max` → Warning-лог + `connections.rejected` + `session.Disconnect()`
  + `return` (НЕ рахуємо в active, НЕ стартуємо span);
- інакше → `connections.active`+1 + старт `Activity`, збереженого у `ConditionalWeakTable<TcpSession,Activity>`
  (маркер «зараховано»).

`OnDisconnected(TcpSession)` (перед `base`): якщо сесія є в CWT (зараховано) → `connections.active`-1 +
`Activity.Stop()` + прибрати з CWT. Інакше (відхилена) → no-op. Балансовані inc/dec.

Резолв конфігу: `ServiceProvider.GetService(typeof(JsonRpcServerConfig))` лінькувато (volatile-кеш). Якщо
конфіг недоступний (сервер без DI) → `Max = 0` (без ліміту).

## Чому не зараз
- **Idle-timeout** — потрібен per-session timestamp на `OnWsReceived`, який споживач перевизначає й НЕ
  кличе `base` (напр. `DemoJsonRpcSession`); прозоро не вийде — це окремий opt-in API (`RecordActivity()`).
- **Graceful drain** — `Stop()` синхронний і рве всі з'єднання; «stop accept + чекати in-flight» вимагає
  власного accept-gate + відстеження активних RPC; окрема зміна.
- Метрики обрано над повноцінним per-RPC span'ом, бо чистого framework-owned seam'у для тривалості ДИСПЕТЧУ
  немає (StreamJsonRpc створює `JsonRpc` у споживацькій сесії); per-RPC трейс прийде, якщо/коли диспетч
  пройде через наш wrapper на обох шляхах.
