---
paths:
  - "src/WsRpcServer/**"
---

# Established patterns (framework internals)

This is a **framework of abstract base classes**: consumers subclass the `Abstract*` types and supply
business logic. Every pattern below exists to keep that extension seam safe and predictable.

## DI composition root

- `Extensions/JsonRpcCoreExtensions.AddJsonRpcCore(...)` is the single composition root. ✅ H1 shipped
  (`composition-and-config`, 1.3.0): the generic overload
  `AddJsonRpcCore<TServer, TSession, TEventProcessor, TSubscriptionManager, TRegistry>(...)` registers
  all 5 core services + builds the concrete server from validated config, with an **idempotency guard**
  (private `JsonRpcCoreMarker` sentinel + `TryAdd*`) so repeated calls are a no-op. The config-only
  overload remains for callers that wire their own services. Guarded by `AddJsonRpcCoreCompositionTests`.
- "One concrete instance, two service roles" → register the concrete type once (`TryAddSingleton<TConcrete>()`),
  then add the interface registration as `sp => sp.GetRequiredService<TConcrete>()`. Don't register the
  concrete type twice. (Exactly what the generic `AddJsonRpcCore` does for the event processor +
  subscription manager.)

## Disposal + cancellation (audit finding H4)

- Types that own a `CancellationTokenSource` for background work (`AbstractJsonRpcSession`,
  `AbstractEventProcessor`) MUST, on dispose: **(1)** `Cts.Cancel()`, **(2)** await/drain the in-flight
  task (swallowing the expected `OperationCanceledException`), **(3)** `Cts.Dispose()`. Disposing the
  Cts without cancelling first orphans background tasks and surfaces `ObjectDisposedException` under
  shutdown/connection-drop.
- Prefer `IAsyncDisposable` for any type with async cleanup (the SignalCli.NET `IJsonRpcClient` is
  `IAsyncDisposable`-only). Never add a sync `Dispose()` that does
  `DisposeAsync().AsTask().GetAwaiter().GetResult()`.
- `SemaphoreSlim` operation-locks (`AbstractSubscriptionManager.OperationLock`) must be drained
  (`WaitAsync`) before disposal, or in-flight `Release()` throws `ObjectDisposedException`.

## Subscription manager (audit findings M2/M3/M4)

- ✅ Shipped in `subscription-manager-cleanup` (2.0.0, BREAKING). `ISubscriptionManager<TEventType, TEventArgs>`
  is generic (no `object` params); `Subscribe` uses `topic` (not `account`); subscribe/update take
  `IReadOnlyCollection<TEventType>`.
- `AbstractSubscriptionManager<TEventType, TEventArgs>` is **template method**: the public
  `Subscribe`/`Unsubscribe`/`UpdateSubscription` are sealed-ish wrappers that run the abstract
  `SubscribeCore`/`UnsubscribeCore`/`UpdateSubscriptionCore` under `OperationLock` (via `WithLockAsync`).
  Derived classes implement only `*Core` and must **never** call the public `Subscribe` from inside a
  `*Core` (the lock is non-reentrant — `SemaphoreSlim(1,1)` — so that self-deadlocks; call the sibling
  `*Core` directly, as `DemoSubscriptionManager.UpdateSubscriptionCore` does).
- `GetClientsForEvent` is the hot read path and intentionally takes **no** `OperationLock`; rely on a
  thread-safe store (`AbstractSubscriptionStore<…>` uses `ReaderWriterLockSlim`) for read concurrency.
- A mutating call after `Dispose` throws `ObjectDisposedException` (typed state error, not a raced
  semaphore failure).

## Reflection service registry (audit finding H3 + AOT)

- `AbstractRpcServiceRegistry` discovers `IRpcService` / `IClientAwareRpcService` via
  `AppDomain.CurrentDomain.GetAssemblies()` + `GetExportedTypes()`. This runs once per connection, so:
  - The lazy type cache MUST be built thread-safely (`Lazy<T>` with `ExecutionAndPublication`, or
    `Interlocked.CompareExchange`). `_cache ??= Build()` is a race when two connections start together.
  - When >1 implementation of the same RPC interface is found, **log a Warning** rather than silently
    taking `FirstOrDefault` — the consumer should disambiguate via DI.
- The reflection scan is **not AOT-compatible** (IL2026/IL3050). Do **not** set
  `<IsAotCompatible>true</IsAotCompatible>` on this project without first providing a source-gen
  discovery alternative — otherwise an AOT-publishing consumer gets warnings/trim failures.

## Transport / parse-recovery (audit finding H2)

- `WebSocketMessageHandler : Stream` adapts WS frames into the byte stream StreamJsonRpc reads. The
  malformed-JSON recovery loop (`FindNextJsonDelimiter`) must be **bounded**: after N consecutive parse
  failures, close the connection with `WebSocketCloseStatus.ProtocolError`. An unbounded recovery loop
  is a single-connection CPU-burn DoS vector.
- `CanRead`/`CanWrite` should reflect disposal state (`=> !_disposed`), not hardcoded `true`, so
  StreamJsonRpc stops calling into a disposed handler.

## Notification fan-out

- `AbstractEventProcessor` fan-outs server→client notifications via a per-client handler registry.
  Fire-and-forget notification tasks must route failures through a failure-counter (auto-unregister a
  client after a threshold) rather than logging-and-forgetting — otherwise a broken client receives
  infinite failed notifications. Use `TryAdd` (not indexer-overwrite) when registering a client and log
  a Warning on duplicate registration.

## Logging (source-generated)

- ✅ Shipped in `logger-message-migration` (2.1.0). All `ILogger` logging in `src/WsRpcServer` goes
  through source-generated `[LoggerMessage]` partial methods, one `internal static partial class
  <Type>Log` per logging type under `src/WsRpcServer/Logging/` — the SignalCli.NET convention.
- **EventId blocks are reserved per type** (don't collide; `LoggerMessageMigrationTests` enforces
  uniqueness): `AbstractJsonRpcServer` 1000–1099, `AbstractJsonRpcSession` 1100–1199,
  `WebSocketMessageHandler` 1200–1299, `AbstractRpcServiceRegistry` 1300–1399,
  `AbstractEventProcessor` 1400–1499. A new logging type gets the next free block.
- **Add a log line by adding a `[LoggerMessage]` method**, not an inline `Logger.LogX("…", …)` — `CA1848`/
  `CA1873` are active in the lib and a direct call fails the build (and `LoggerMessageMigrationTests`).
  Call sites read `<Type>Log.MethodName(Logger, …)`; the `Exception` argument (if any) goes immediately
  after `ILogger logger`. Preserve message text + level + structured property names verbatim when editing.
- **Mocking gotcha (tests):** the generated methods guard with `logger.IsEnabled(level)` first; a default
  `Mock<ILogger>` returns `false` from `IsEnabled`, so a verify-on-`Log` test silently sees zero calls.
  Set `mock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true)` after creating the mock.

## Configuration (audit finding M5)

- ✅ M5 shipped (`composition-and-config`, 1.3.0): `JsonRpcServerConfig` carries `[Range]`/`[Required]`
  DataAnnotations + a source-gen `[OptionsValidator]` (`JsonRpcServerConfigValidator`, reflection-free)
  wired through `AddJsonRpcCore` via `AddOptionsWithValidateOnStart`, with a `.Validate(...)` cross-field
  rule for the `TimeSpan` `NotificationTimeout` (no DataAnnotation fits it). Validated fail-fast
  (`Port` ∈ [1,65535], `NotificationQueueSize`/`MaxMessageSizeBytes`/`PipeThresholdBytes`/
  `MaxConsecutiveParseFailures` ≥ 1, non-empty `Host`, positive `NotificationTimeout`). The direct
  `Microsoft.Extensions.Options` package reference is required so the source generator runs (analyzers
  don't flow transitively). Don't re-add reflection-based `.ValidateDataAnnotations()` alongside it.
  Guarded by `JsonRpcServerConfigValidationTests`.
