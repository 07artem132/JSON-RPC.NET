# Композиція DI + конфігурація — `JsonRpcCoreExtensions` та `JsonRpcServerConfig`

Реєстрація фреймворку у `Microsoft.Extensions.DependencyInjection` і повний reference властивостей
`JsonRpcServerConfig`. Композиційний корінь живе **у бібліотеці** (не у споживача) — це CLAUDE.md
rule #6 (H1, `composition-and-config` 1.3.0).

---

## `JsonRpcCoreExtensions`

Статичний клас із двома перевантаженнями `AddJsonRpcCore`. Обидва **ідемпотентні**: повторний виклик
тихо no-op'ує через private sentinel-маркер `JsonRpcCoreMarker` + `TryAdd*` (rule #6). Тож споживач
може зареєструвати власну реалізацію будь-якого з 5 сервісів **до** виклику — вона не перетреться.

### `AddJsonRpcCore(Action<JsonRpcServerConfig>?)` — лише конфігурація

```csharp
public static IServiceCollection AddJsonRpcCore(
    this IServiceCollection services,
    Action<JsonRpcServerConfig>? configureOptions = null);
```

Реєструє **тільки** конфіг — через options-pipeline із fail-fast валідацією, без жодного з 5 core-сервісів.
Використовуй, якщо хочеш зв'язати сервер/сесію/реєстр власноруч. Конфіг доступний у DI у двох ролях:

1. `IOptions<JsonRpcServerConfig>` із валідацією `ValidateOnStart` (`JsonRpcServerConfigValidator`
   source-gen DataAnnotations + крос-польове правило `NotificationTimeout > TimeSpan.Zero`);
2. напряму резолвабельний `JsonRpcServerConfig` (значення з валідованих опцій) — back-compat для коду,
   що ін'єктить конфіг конструктором.

Невалідний конфіг кидає `OptionsValidationException` на `host.StartAsync()` / першому резолві (rule #7).

### `AddJsonRpcCore<TServer, TSession, TEventProcessor, TSubscriptionManager, TRegistry, TEventType, TEventArgs>(…)` — повний композиційний корінь

```csharp
public static IServiceCollection AddJsonRpcCore<
    TServer, TSession, TEventProcessor, TSubscriptionManager, TRegistry, TEventType, TEventArgs>(
    this IServiceCollection services,
    Action<JsonRpcServerConfig>? configureOptions = null)
    where TServer : AbstractJsonRpcServer
    where TSession : AbstractJsonRpcSession
    where TEventProcessor : class, IEventProcessor
    where TSubscriptionManager : class, ISubscriptionManager<TEventType, TEventArgs>
    where TRegistry : class, IRpcServiceRegistry;
```

Реєструє **всі 5 core-сервісів + конкретний сервер** одним викликом. Споживач більше не зв'язує їх руками.

| Параметр-тип | Базовий контракт | Роль |
|---|---|---|
| `TServer` | `AbstractJsonRpcServer` | приймає WS-з'єднання, створює сесії |
| `TSession` | `AbstractJsonRpcSession` | життєвий цикл одного з'єднання |
| `TEventProcessor` | `IEventProcessor` | fan-out server→client сповіщень |
| `TSubscriptionManager` | `ISubscriptionManager<TEventType, TEventArgs>` | облік підписок |
| `TRegistry` | `IRpcServiceRegistry` | discovery + реєстрація RPC-сервісів |
| `TEventType`, `TEventArgs` | — | типобезпека менеджера підписок (M4) |

Деталі реєстрації:

- **«Один екземпляр — дві ролі»** для event-processor'а та subscription-manager'а: конкретний тип як
  singleton + інтерфейс резолвиться як той самий екземпляр (`sp => sp.GetRequiredService<TConcrete>()`).
- **`TSession` — transient** (по екземпляру на з'єднання), решта — singleton.
- **`TServer` будується з валідованого конфігу**: `Host` → `IPAddress.Parse`, далі
  `ActivatorUtilities.CreateInstance<TServer>(sp, ipAddress, config.Port, sp, logger)` — тобто конструктор
  твого сервера повинен мати сигнатуру `(IPAddress, int, IServiceProvider, ILogger<TServer>)`
  (див. [`server-and-session.md`](server-and-session.md)).
- 5 generic-параметрів сервісів несуть `[DynamicallyAccessedMembers(PublicConstructors)]` — щоб шлях
  discovery був AOT-clean (rule #4; деталі — [`aot.md`](../aot.md)).

```csharp
services.AddJsonRpcCore<
    DemoJsonRpcServer,
    DemoJsonRpcSession,
    DemoEventProcessor,
    DemoSubscriptionManager,
    DemoServiceRegistry,
    ServerEventType,        // TEventType
    object>(                // TEventArgs
    options =>
    {
        options.Host = "0.0.0.0";
        options.Port = 9000;
    });
```

> **Запуск.** `AddJsonRpcCore` лише реєструє. Старт — вручну: зарезолв `IEventProcessor` →
> `StartAsync`, зарезолв `TServer` → `Start()`. Повний flow — у [`examples/echo-server.md`](../examples/echo-server.md).

---

## `JsonRpcServerConfig` — властивості

`record` (для `with`-expressions + value-рівності). Валідація — DataAnnotations нижче +
крос-польове правило для `NotificationTimeout` (для `TimeSpan` немає влучного DataAnnotation).

| Властивість | Тип | Дефолт | Валідація | Опис |
|---|---|---|---|---|
| `Host` | `string` | `"0.0.0.0"` | `[Required(AllowEmptyStrings=false)]` | Адреса прослуховування; `0.0.0.0` = всі інтерфейси. Парситься через `IPAddress.Parse` при побудові сервера |
| `Port` | `int` | `9000` | `[Range(1, 65535)]` | TCP-порт |
| `MaxMessageSizeBytes` | `int` | `104857600` (100 МБ) | `[Range(1, int.MaxValue)]` | Захист від «JSON-бомби» / неконтрольованого споживання пам'яті |
| `NotificationQueueSize` | `int` | `1000` | `[Range(1, int.MaxValue)]` | Ємність per-client bounded-каналу сповіщень. `BoundedChannelFullMode.DropOldest` (back-pressure: найстаріші drop'аються) |
| `PipeThresholdBytes` | `int` | `1048576` (1 МБ) | `[Range(1, int.MaxValue)]` | `System.IO.Pipelines` pause-writer threshold вхідного потоку транспорту |
| `NotificationTimeout` | `TimeSpan` | `5 s` | крос-польове: `> TimeSpan.Zero` | Таймаут на надсилання одного сповіщення проблемному клієнту |
| `MaxConsecutiveParseFailures` | `int` | `10` | `[Range(1, int.MaxValue)]` | Поріг послідовних помилок розбору JSON, після якого з'єднання примусово закривається з `ProtocolError` (anti-DoS, rule #5). Скидається після кожного успішного розбору. Деталі — [`errors.md`](errors.md) |
| `MaxConcurrentConnections` | `int` | `0` | `[Range(0, int.MaxValue)]` | Квота одночасних з'єднань; `0` = без ліміту. Коли `> 0`, сервер відхиляє з'єднання понад поріг (anti-DoS). Деталі — [`observability.md`](observability.md) |

```csharp
services.AddJsonRpcCore<…>(o =>
{
    o.Host = "127.0.0.1";
    o.Port = 9000;
    o.MaxMessageSizeBytes = 16 * 1024 * 1024;     // 16 МБ
    o.NotificationQueueSize = 256;
    o.NotificationTimeout = TimeSpan.FromSeconds(2);
    o.MaxConsecutiveParseFailures = 5;
});
```

---

## Реєстраційний граф (що отримаєш у DI після generic `AddJsonRpcCore<…>`)

```
IEventProcessor                          → TEventProcessor (один singleton, дві ролі)
ISubscriptionManager<TEventType,TArgs>   → TSubscriptionManager (один singleton, дві ролі)
IRpcServiceRegistry                      → TRegistry (singleton)
TSession                                 → transient (екземпляр на з'єднання)
TServer                                  → singleton (побудований з валідованого конфігу)

IOptions<JsonRpcServerConfig>            → validated на host.StartAsync()
JsonRpcServerConfig                      → значення з валідованих опцій (back-compat пряма ін'єкція)
```

Опціонально (opt-in для AOT): `AddGeneratedRpcServiceCatalog()` + `AddGeneratedRpcMethodBinder()`
додають reflection-free `IRpcServiceCatalog` / `IRpcMethodBinder` — див.
[`services-and-registry.md`](services-and-registry.md) та [`aot.md`](../aot.md).
