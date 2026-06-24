# Документація JSON-RPC.NET (WsRpcServer)

Повний reference фреймворку абстрактних базових класів для двобічних **JSON-RPC 2.0** сервісів
поверх WebSocket. Швидкий старт + установка живуть у [`/README.md`](../README.md); ця папка — для
глибшого читання per-тип.

> **Що це за бібліотека.** Не «сервіс із готовими методами», а **фреймворк**: ти створюєш нащадків
> `Abstract*`-типів і реєструєш RPC-сервіси через discovery `IRpcService`. Тому reference нижче
> описує **точки розширення** (що перевизначати, що викликати, які інваріанти не порушити), а не
> фіксований набір ендпоінтів.

## API reference (per-категорія)

| Файл | Що покриває |
|---|---|
| [`api/composition-and-config.md`](api/composition-and-config.md) | `JsonRpcCoreExtensions` (`AddJsonRpcCore` — конфіг-only + generic-композиційний корінь) + повний reference `JsonRpcServerConfig` |
| [`api/server-and-session.md`](api/server-and-session.md) | `AbstractJsonRpcServer`, `AbstractJsonRpcSession`, `IJsonRpcSession`, транспортний `WebSocketMessageHandler` |
| [`api/services-and-registry.md`](api/services-and-registry.md) | `IRpcService` / `IClientAwareRpcService` discovery, `AbstractRpcServiceRegistry`, `IRpcServiceRegistry` + source-gen шлях (`IRpcServiceCatalog`, `IRpcMethodBinder`, `[GenerateRpcServiceCatalog]`, `RpcServiceDescriptor`) |
| [`api/subscriptions.md`](api/subscriptions.md) | `ISubscriptionManager<TEventType,TEventArgs>` + `AbstractSubscriptionManager<…>` (template-method + `OperationLock`) + `ISubscriptionStore<…>` / `AbstractSubscriptionStore<…>` |
| [`api/events.md`](api/events.md) | `IEventProcessor` + `AbstractEventProcessor` (fan-out server→client, auto-unregister) + `RpcNotification` |
| [`api/errors.md`](api/errors.md) | `RpcErrorException` + `JsonRpcErrorCode` (структуровані помилки) + bounded parse-recovery транспорту |

## Приклади

| Файл | Що покриває |
|---|---|
| [`examples/echo-server.md`](examples/echo-server.md) | End-to-end: усі 6 точок розширення з'єднані в робочий сервер (на базі `example/SimpleServer`) |

## Operational

| Файл | Що покриває |
|---|---|
| [`aot.md`](aot.md) | Native-AOT / trim: чому discovery + dispatch вже AOT-clean, як це увімкнути (`AddGeneratedRpcServiceCatalog()` + `AddGeneratedRpcMethodBinder()`), і чому `<IsAotCompatible>` досі вимкнено (upstream StreamJsonRpc) |

## Convention для нових docs

- Middle-depth опис per-тип: коротко *що це*, signature точок розширення, *що перевизначати vs
  викликати*, приклад, і — замість «signal-cli source citation» у sibling-репо SignalCli.NET — **посилання
  на CLAUDE.md «Critical rule #N» + OpenSpec-капабіліті/версію**, де інваріант зашиплено (напр.
  «rule #3, `security-hardening` 1.2.0»).
- Кожен публічний тип збірки `WsRpcServer` мусить бути згаданий хоча б в одному файлі під `docs/api/` —
  це enforce'иться regression-guard'ом `DocsApiCoverageTests` (analog RG09 із SignalCli.NET): новий
  публічний тип без згадки в docs валить білд тестів.
- Мова — українська, як решта репо (`README.md`, коментарі, лог-меседжі). Назви типів/методів — як у коді.
