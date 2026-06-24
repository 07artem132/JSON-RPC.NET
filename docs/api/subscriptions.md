# Підписки — `ISubscriptionManager<…>`, `AbstractSubscriptionManager<…>`, `ISubscriptionStore<…>`, `AbstractSubscriptionStore<…>`

Subscription-шар (5 із п'яти): облік того, **хто** на **які події** підписаний. Менеджер серіалізує
мутації під замком; сховище — потокобезпечне читання на гарячому шляху доставки.

> **Generic, без `object`** (rule #8, M2/M3/M4, `subscription-manager-cleanup` 2.0.0 BREAKING):
> інтерфейс параметризований `TEventType`/`TEventArgs` (типобезпека замість `object`), і сегмент
> підписки зветься `topic`, а не доменно-специфічним `account`.

---

## `ISubscriptionManager<TEventType, TEventArgs>`

```csharp
public interface ISubscriptionManager<TEventType, TEventArgs> : IDisposable
{
    Task<int> Subscribe(Guid clientId, string topic,
        IReadOnlyCollection<TEventType> eventTypes, CancellationToken ct = default);
    Task<bool> Unsubscribe(Guid clientId, int subscriptionId, CancellationToken ct = default);
    Task<bool> UpdateSubscription(Guid clientId, int subscriptionId,
        IReadOnlyCollection<TEventType> eventTypes, CancellationToken ct = default);
    List<Guid> GetClientsForEvent(TEventArgs args, TEventType eventType);
}
```

| Член | Призначення |
|---|---|
| `Subscribe` | Створює підписку; повертає `subscriptionId` для подальших операцій |
| `Unsubscribe` | Знімає підписку за id; `true` якщо успішно |
| `UpdateSubscription` | Замінює типи подій існуючої підписки |
| `GetClientsForEvent` | **Гарячий шлях читання**: які клієнти мають отримати цю подію. Без замка |

- `TEventType` — що ідентифікує вид події (enum / рядок).
- `TEventArgs` — за чим фільтруються підписки в `GetClientsForEvent`.

---

## `AbstractSubscriptionManager<TEventType, TEventArgs>`

**Template Method**: публічні методи — це обгортки, що ганяють абстрактні `*Core` під `OperationLock`.
Нащадок реалізує лише бізнес-логіку `*Core`.

```csharp
public abstract class AbstractSubscriptionManager<TEventType, TEventArgs>(
    ILogger logger, int maxSubscriptionsPerClient = 10)
    : ISubscriptionManager<TEventType, TEventArgs>
{
    protected ILogger Logger { get; }
    protected SemaphoreSlim OperationLock { get; }                       // SemaphoreSlim(1,1) — НЕ реентрантний
    protected ConcurrentDictionary<Guid, int> ClientSubscriptionCounts { get; }
    protected int MaxSubscriptionsPerClient { get; }
    protected bool IsDisposed { get; set; }

    protected async Task<TResult> WithLockAsync<TResult>(Func<Task<TResult>> action, …);

    // Публічні — sealed-подібні обгортки (серіалізують через OperationLock):
    public Task<int>  Subscribe(Guid clientId, string topic, IReadOnlyCollection<TEventType> types, CancellationToken ct = default);
    public Task<bool> Unsubscribe(Guid clientId, int subscriptionId, CancellationToken ct = default);
    public Task<bool> UpdateSubscription(Guid clientId, int subscriptionId, IReadOnlyCollection<TEventType> types, CancellationToken ct = default);

    // Абстрактні — реалізуй ЦЕ (виконуються вже під OperationLock):
    protected abstract Task<int>  SubscribeCore(Guid clientId, string topic, IReadOnlyCollection<TEventType> types, CancellationToken ct);
    protected abstract Task<bool> UnsubscribeCore(Guid clientId, int subscriptionId, CancellationToken ct);
    protected abstract Task<bool> UpdateSubscriptionCore(Guid clientId, int subscriptionId, IReadOnlyCollection<TEventType> types, CancellationToken ct);

    public abstract List<Guid> GetClientsForEvent(TEventArgs args, TEventType eventType);  // лишається lock-free
}
```

Критичні правила (rule #8):

- **`OperationLock` не реентрантний** (`SemaphoreSlim(1,1)`). НЕ викликай публічний `Subscribe` зсередини
  будь-якого `*Core` — це самодедлок. Як треба з `UpdateSubscriptionCore` повторно підписати — клич
  **сусідній `*Core` напряму** (`SubscribeCore(...)`).
- **`GetClientsForEvent` навмисно без замка** — гарячий шлях читання. Покладайся на потокобезпечне
  сховище (нижче), не на write-орієнтований `OperationLock`.
- **`ClientSubscriptionCounts` + `MaxSubscriptionsPerClient`** — це сховище+політика, які база надає, а
  **нащадок використовує** для enforcement per-client cap (документований split «база зберігає, нащадок
  застосовує»; це не мертвий код — його читає реальний споживач `SignalCliNet.WsRpcServer`, див.
  `.claude/rules/audit-debt.md` R2-M3).
- **Мутація після `Dispose` → `ObjectDisposedException`** (типова state-помилка, не раса семафора).
  `Dispose` дренує `OperationLock` перед звільненням (rule #3).

```csharp
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

    protected override Task<bool> UpdateSubscriptionCore(
        Guid clientId, int subscriptionId, IReadOnlyCollection<ServerEventType> types, CancellationToken ct)
    {
        _ = SubscribeCore(clientId, string.Empty, types, ct);   // ← *Core напряму, бо вже під OperationLock
        return Task.FromResult(true);
    }

    // UnsubscribeCore … опущено

    public override List<Guid> GetClientsForEvent(object args, ServerEventType eventType) =>
        _map.Where(kv => kv.Value.Contains(eventType)).Select(kv => kv.Key).ToList();
}
```

---

## `ISubscriptionStore<TSubscription, in TEventArgs, in TEventType>`

Опціональне потокобезпечне сховище для гарячого шляху читання — окреме від менеджера, щоб
`GetClientsForEvent` не брав write-замок.

```csharp
public interface ISubscriptionStore<TSubscription, in TEventArgs, in TEventType> : IDisposable
{
    void AddSubscription(TSubscription subscription, int providerSubscriptionId);
    TSubscription? GetSubscription(int subscriptionId);
    (TSubscription? Subscription, HashSet<Guid>? RemainingClients, int ProviderSubscriptionId)
        RemoveSubscription(Guid clientId, int subscriptionId);
    void UpdateSubscription(TSubscription subscription);
    List<int> GetClientSubscriptionIds(Guid clientId);
    Dictionary<int, string> GetClientSubscriptionsInfo(Guid clientId);
    int GenerateSubscriptionId();
    (string? Account, int? ProviderSubscriptionId) GetSubscriptionInfo(int subscriptionId);
}
```

---

## `AbstractSubscriptionStore<TSubscription, TEventArgs, TEventType>`

Базова реалізація сховища (теж Template Method: публічні члени під `ReaderWriterLockSlim`, нащадок
реалізує `*Core`). `GetClientsForEvent` тут — read-lock, тож менеджерів гарячий шлях справді
конкурентний на читання.

```csharp
public abstract class AbstractSubscriptionStore<TSubscription, TEventArgs, TEventType>
    : ISubscriptionStore<TSubscription, TEventArgs, TEventType>
{
    // Публічні (під ReaderWriterLockSlim): AddSubscription / GetSubscription / RemoveSubscription /
    //   UpdateSubscription / GetClientSubscriptionIds / GetClientSubscriptionsInfo /
    //   GetClientsForEvent / GetSubscriptionInfo / GenerateSubscriptionId / Dispose
    // Абстрактні (реалізуй ЦЕ): *Core-двійники кожного з мутаторів/читачів.
    protected abstract void AddSubscriptionCore(TSubscription subscription, int providerSubscriptionId);
    protected abstract TSubscription? GetSubscriptionCore(int subscriptionId);
    protected abstract (TSubscription?, HashSet<Guid>?, int) RemoveSubscriptionCore(Guid clientId, int subscriptionId);
    protected abstract void UpdateSubscriptionCore(TSubscription subscription);
    protected abstract List<int> GetClientSubscriptionIdsCore(Guid clientId);
    protected abstract Dictionary<int, string> GetClientSubscriptionsInfoCore(Guid clientId);
    protected abstract List<Guid> GetClientsForEventCore(TEventArgs args, TEventType eventType);
    protected abstract (string?, int?) GetSubscriptionInfoCore(int subscriptionId);
}
```

`GenerateSubscriptionId()` видає монотонні id (старт із 1). Для простих демо сховище можна не
використовувати — тримати стан прямо в менеджері (як `DemoSubscriptionManager` вище); сховище потрібне,
коли read-конкурентність гарячого шляху стає вузьким місцем.
