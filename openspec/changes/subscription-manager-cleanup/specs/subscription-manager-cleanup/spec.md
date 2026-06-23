# Spec — subscription-manager-cleanup

## ADDED Requirements

### Requirement: The subscription manager SHALL be generic and object-free (M3 + M4)

`ISubscriptionManager<TEventType, TEventArgs>` SHALL replace the non-generic `ISubscriptionManager`.
`Subscribe`/`UpdateSubscription` SHALL take `IReadOnlyCollection<TEventType> eventTypes` and `Subscribe`
SHALL name its scope parameter `topic` (not `account`). `GetClientsForEvent(TEventArgs args, TEventType eventType)`
SHALL be strongly typed. No member SHALL use `object` for event types/args.

#### Scenario: Type-safe subscribe and match

- **GIVEN** a `AbstractSubscriptionManager<string, object>` subclass
- **WHEN** `Subscribe(clientId, topic, ["Create", "Update"])` is called and then
  `GetClientsForEvent(args, "Update")`
- **THEN** the call compiles without casts and returns the subscribed client

### Requirement: The base SHALL serialize mutations through OperationLock (M2)

`AbstractSubscriptionManager<TEventType, TEventArgs>` SHALL expose the public `Subscribe`/`Unsubscribe`/
`UpdateSubscription` as template methods that invoke abstract `SubscribeCore`/`UnsubscribeCore`/
`UpdateSubscriptionCore` under `OperationLock` (via a `WithLockAsync` helper). `GetClientsForEvent` SHALL
remain abstract and lock-free (it is the hot read path; concurrency is the store's responsibility). The
existing dispose-drain of `OperationLock` (critical rule #3) SHALL be preserved.

#### Scenario: A held lock blocks a mutation

- **GIVEN** an external holder has acquired `OperationLock`
- **WHEN** `Subscribe(...)` is called
- **THEN** the returned task does not complete until the holder releases the lock, after which it proceeds

#### Scenario: Mutating after dispose is a typed error

- **GIVEN** a disposed subscription manager
- **WHEN** `Subscribe(...)` is called
- **THEN** an `ObjectDisposedException` is thrown (not a raced `SemaphoreSlim` failure)

### Requirement: The composition root SHALL register the generic interface

The generic
`AddJsonRpcCore<TServer, TSession, TEventProcessor, TSubscriptionManager, TRegistry, TEventType, TEventArgs>`
overload (with `TSubscriptionManager : class, ISubscriptionManager<TEventType, TEventArgs>`) SHALL register
`ISubscriptionManager<TEventType, TEventArgs>` resolving to the same singleton as the concrete
`TSubscriptionManager`.

#### Scenario: Generic interface resolves to the concrete singleton

- **GIVEN** `AddJsonRpcCore<…, string, object>()`
- **WHEN** `ISubscriptionManager<string, object>` and the concrete manager are both resolved
- **THEN** they are the same instance
