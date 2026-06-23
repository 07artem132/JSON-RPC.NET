---
paths:
  - "src/WsRpcServer/**"
---

# Established patterns (framework internals)

This is a **framework of abstract base classes**: consumers subclass the `Abstract*` types and supply
business logic. Every pattern below exists to keep that extension seam safe and predictable.

## DI composition root

- `Extensions/JsonRpcCoreExtensions.AddJsonRpcCore(...)` is the single composition root. Today it
  registers only `JsonRpcServerConfig` — the consumer hand-wires the other 5 services
  (`IRpcServiceRegistry`, `ISubscriptionManager`, `IEventProcessor`, the concrete server + session).
  That's audit finding **H1**: the goal state is a generic-parameterized overload that registers all
  core services + the concrete server, plus an **idempotency guard** (a private marker type, the
  SignalCli.NET `SignalCliRegistrationMarker` pattern) so repeated `AddJsonRpcCore` calls are a no-op.
- "One concrete instance, two service roles" → register the concrete type once, then add the interface
  registration as `sp => sp.GetRequiredService<TConcrete>()`. Don't register the concrete type twice.

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

## Configuration (audit finding M5)

- `JsonRpcServerConfig` currently has no validation. The goal state mirrors SignalCli.NET's options
  pattern: `[Range]`/`[Required]` DataAnnotations + `IOptions<T>` + source-gen `[OptionsValidator]`,
  validated fail-fast on host start (`Port` ∈ [1,65535], `NotificationQueueSize` ≥ 1, non-empty `Host`).
