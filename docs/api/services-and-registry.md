# RPC-сервіси та реєстр — discovery, `AbstractRpcServiceRegistry`, source-gen шлях

Service-шар (4 із п'яти): як ти оголошуєш RPC-методи й як фреймворк їх знаходить та чіпляє до
`JsonRpc`. Два шляхи виявлення: рефлексійне сканування (за замовчуванням) та reflection-free
source-gen каталог (opt-in, AOT-clean).

---

## `IRpcService` — маркер звичайного сервісу

```csharp
public interface IRpcService { }
```

Маркерний інтерфейс. Будь-який не-абстрактний клас, що реалізує інтерфейс-нащадок `IRpcService`, стає
RPC-сервісом: його методи `Task<T> Method(args…)` доступні клієнту по JSON-RPC (імена — camelCase).

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

// Реєструється у DI як звичайний сервіс:
services.AddSingleton<ICalculatorService, CalculatorService>();
```

Звичайні сервіси резолвляться з DI (singleton/scoped/transient — як зареєстрував).

---

## `IClientAwareRpcService` — сервіс, що знає свого клієнта

```csharp
public interface IClientAwareRpcService : IRpcService { }
```

Маркер для сервісів, яким потрібен `Guid clientId` поточного з'єднання. Реєстр конструює **новий
екземпляр на клієнта**, передаючи `clientId` у конструктор (єдиний ctor: параметр `Guid` → clientId,
решта → з DI). Так RPC-метод знає, **хто** його викликав — типовий випадок для підписок:

```csharp
public interface IDemoEventsRpc : IClientAwareRpcService
{
    Task<int> Subscribe(string topic, ServerEventType[] eventTypes, CancellationToken ct = default);
    Task<bool> Unsubscribe(int subscriptionId, CancellationToken ct = default);
    Task<bool> UpdateSubscription(int subscriptionId, ServerEventType[] eventTypes, CancellationToken ct = default);
}

public class DemoEventsRpcAdapter(
    ISubscriptionManager<ServerEventType, object> subs,
    ILogger<DemoEventsRpcAdapter> logger,
    Guid clientId) : IDemoEventsRpc          // ← clientId інжектиться реєстром, не з DI
{
    public Task<int> Subscribe(string topic, ServerEventType[] types, CancellationToken ct = default) =>
        subs.Subscribe(clientId, topic, types, ct);
    // …
}
```

---

## `AbstractRpcServiceRegistry`

```csharp
public abstract class AbstractRpcServiceRegistry(IServiceProvider serviceProvider, ILogger logger)
    : IRpcServiceRegistry
{
    protected IServiceProvider ServiceProvider { get; }
    protected ILogger Logger { get; }
    protected JsonRpcTargetOptions StandardOptions { get; }              // camelCase + ін.
    public virtual void RegisterServices(JsonRpc jsonRpc, Guid clientId);
    protected virtual Assembly[] GetTargetAssemblies();
    protected virtual bool IsTargetAssembly(Assembly assembly);
    protected abstract IEnumerable<string> GetAdditionalAssemblyPrefixes();  // ← єдине обов'язкове
}
```

Єдиний обов'язковий override — `GetAdditionalAssemblyPrefixes()`: поверни префікси збірок, які треба
сканувати на RPC-сервіси (зазвичай — твоя збірка):

```csharp
public class DemoServiceRegistry(IServiceProvider sp, ILogger<DemoServiceRegistry> logger)
    : AbstractRpcServiceRegistry(sp, logger)
{
    protected override IEnumerable<string> GetAdditionalAssemblyPrefixes() => ["SimpleServer"];
}
```

`RegisterServices(jsonRpc, clientId)` викликається сесією у `OnWsConnected`. Він:

- **звичайні сервіси** резолвить із DI і чіпляє до `JsonRpc`;
- **client-aware сервіси** конструює з `clientId`;
- порядок виявлення: **спершу `IRpcMethodBinder`** (якщо зареєстрований — AOT-clean dispatch), інакше
  рефлексійний `AddLocalRpcTarget` (`RegisterServicesViaReflection`, анотований
  `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]`).

Інваріанти (rule #4, H3):

- Рефлексійний type-cache будується **один раз** потокобезпечно (volatile + double-checked `Lock`).
- Кілька реалізацій одного інтерфейсу → **Warning**, а не тихий first-wins.
- AOT/trim: дивись source-gen шлях нижче + [`aot.md`](../aot.md).

---

## `IRpcServiceRegistry`

```csharp
public interface IRpcServiceRegistry
{
    void RegisterServices(JsonRpc jsonRpc, Guid clientId);
}
```

Контракт реєстру (те, що ін'єктить сесія). `AddJsonRpcCore<…>` реєструє твій `TRegistry` саме як цей інтерфейс.

---

## Source-gen шлях (reflection-free, AOT-clean) — opt-in

Альтернатива рефлексії: source-генератор `WsRpcServer.SourceGenerator` (їде всередині nupkg як
analyzer) на етапі компіляції будує два артефакти. Вмикається на збірці-споживачі.

### `[GenerateRpcServiceCatalog]` (assembly-level)

```csharp
[assembly: GenerateRpcServiceCatalog]
```

```csharp
public sealed class GenerateRpcServiceCatalogAttribute : Attribute;
```

Позначає збірку: генератор просканує її на RPC-сервіси під час компіляції й згенерує реалізації
`IRpcServiceCatalog` + `IRpcMethodBinder` + два DI-extension'и.

### `IRpcServiceCatalog` — виявлення без рефлексії

```csharp
public interface IRpcServiceCatalog
{
    IReadOnlyList<RpcServiceDescriptor> Services { get; }
}
```

Якщо зареєстрований у DI (`services.AddGeneratedRpcServiceCatalog()`),
`AbstractRpcServiceRegistry` бере перелік сервісів **з нього** і не виконує рефлексійного
`GetExportedTypes`/`IsAssignableFrom`. Інакше — fallback на рефлексію (стара поведінка без змін).

### `RpcServiceDescriptor` — один запис каталогу

```csharp
public readonly record struct RpcServiceDescriptor(
    Type InterfaceType, Type ImplementationType, bool IsClientAware);
```

### `IRpcMethodBinder` — dispatch без рефлексії

```csharp
public interface IRpcMethodBinder
{
    void Bind(JsonRpc jsonRpc, IServiceProvider serviceProvider, Guid clientId);
}
```

Якщо зареєстрований (`services.AddGeneratedRpcMethodBinder()`), `RegisterServices` чіпляє кожен
RPC-метод через `JsonRpc.AddLocalRpcMethod(name, delegate)` (без AOT-атрибутів) замість рефлексійного
`AddLocalRpcTarget`. Генератор шанує `[JsonRpcMethod]`/`[JsonRpcIgnore]` + camelCase; непідтримувані
форми методів (generic/ref/out/in/>16 параметрів) → діагностика `WSRPC002` і пропуск.

> ⚠ **Binder ≠ 1:1 з `AddLocalRpcTarget`** (rule #4): він виставляє лише **методи інтерфейсу** й не
> підтримує target-події / `RpcMarshalable` / `JsonRpcTargetOptions`. Кому це потрібно — лишай
> рефлексійний шлях (не реєструй binder). Дублі імен → `WSRPC001` (per-interface) / `WSRPC003`
> (крос-сервісні колізії, бо всі сервіси чіпляються до одного `JsonRpc`).

Повний AOT-контекст (що вже clean, що блокує upstream) — [`aot.md`](../aot.md).
