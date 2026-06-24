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

## Service registry: source-gen catalog first, reflection fallback (audit finding H3 + AOT)

- ✅ Shipped `registry-sourcegen-discovery` (2.3.0). `AbstractRpcServiceRegistry.BuildServiceTypeCache`
  first resolves `IRpcServiceCatalog` from `ServiceProvider`. If present (consumer opted in with
  `[assembly: GenerateRpcServiceCatalog]` + `AddGeneratedRpcServiceCatalog()`), the cache is built from
  the **compile-time** catalog — **no reflection**. Otherwise it falls back to the reflection scan.
- The generator lives in `src/WsRpcServer.SourceGenerator` (`netstandard2.0`, `IIncrementalGenerator`),
  is referenced by the library `OutputItemType="Analyzer" ReferenceOutputAssembly="false"`, and is packed
  into the nupkg under `analyzers/dotnet/cs` so NuGet consumers get it automatically. It mirrors the
  reflection convention (any non-abstract class implementing an `IRpcService`-derived interface; client-aware
  = derives `IClientAwareRpcService`) and reports `WSRPC001` on duplicate implementations.
- The reflection fallback (`BuildServiceTypeCacheFromReflection`) keeps the thread-safe lazy cache
  (volatile + double-checked `Lock`) and the multi-impl Warning (don't silently `FirstOrDefault`), and is
  annotated `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]`. The dispatcher `BuildServiceTypeCache` and
  `GetTargetAssemblies` carry `[UnconditionalSuppressMessage]` (IL2026/IL3050/IL3000) justified by "the
  catalog is the AOT path; reflection runs only when no catalog is registered" — so a catalog consumer's
  trim/AOT publish is warning-free. ✅ `aot-readiness` (2.4.0).
- **The discovery path is provably Native-AOT-clean.** `AddJsonRpcCore<…>`'s 5 service generics carry
  `[DynamicallyAccessedMembers(PublicConstructors)]`; the library shows **0 IL warnings** under
  `-p:IsAotCompatible=true`; and `aot-smoke/` is a real `PublishAot` native binary that runs catalog-based
  discovery (exit 0, no reflection). Re-measure with
  `dotnet build src/WsRpcServer/WsRpcServer.csproj -p:IsAotCompatible=true -p:TreatWarningsAsErrors=false`.
- **Dispatch is also AOT-clean (opt-in).** ✅ `aot-rpc-dispatch` (2.5.0): the generator also emits
  `RpcMethodBinder : IRpcMethodBinder` (+ separate `AddGeneratedRpcMethodBinder()`). `RegisterServices`
  prefers an injected `IRpcMethodBinder` and binds each RPC-interface method via
  `JsonRpc.AddLocalRpcMethod(name, delegate)` (no AOT attributes) instead of the reflective
  `AddLocalRpcTarget` (now `RegisterServicesViaReflection`, annotated + suppressed at the boundary). The
  binder is a **separate** opt-in from the catalog so existing consumers don't silently change dispatch.
  Generator details: camelCase names (or `[JsonRpcMethod]` override), `[JsonRpcIgnore]` skipped, regular
  services resolved from DI, client-aware constructed (single ctor: `Guid`→clientId, else GetRequiredService),
  unsupported method shapes (generic/ref/out/in/>16 params) → `WSRPC002` + skipped.
- **Trade-off (documented):** the binder exposes only **interface** methods and drops `AddLocalRpcTarget`
  features (target events, `RpcMarshalable`, `JsonRpcTargetOptions`). Consumers needing those keep the
  reflection path (don't register the binder).
- **Still do NOT set `<IsAotCompatible>true</IsAotCompatible>` on the library.** Even with discovery + dispatch
  AOT-clean, StreamJsonRpc 2.25.29's own formatter/envelope serialization isn't AOT-clean (publish flags
  `IL3053`/`IL2104` on `StreamJsonRpc.dll` + transitive `Newtonsoft.Json`); end-to-end AOT RPC payloads are
  the remaining **upstream** gap. Replacing StreamJsonRpc wholesale was researched and rejected.

## Transport / parse-recovery (audit finding H2)

- `WebSocketMessageHandler : Stream` adapts WS frames into the byte stream StreamJsonRpc reads. The
  malformed-JSON recovery loop (`FindNextJsonDelimiter`) must be **bounded**: after N consecutive parse
  failures, close the connection with `WebSocketCloseStatus.ProtocolError`. An unbounded recovery loop
  is a single-connection CPU-burn DoS vector.
- `CanRead`/`CanWrite` should reflect disposal state (`=> !_disposed`), not hardcoded `true`, so
  StreamJsonRpc stops calling into a disposed handler.

## Notification fan-out (audit findings M1/L1/L2)

- ✅ Shipped in `low-severity-polish` (2.2.0). `AbstractEventProcessor` fan-outs server→client
  notifications via a per-client handler registry. Fire-and-forget notification tasks route failures
  through a built-in consecutive-failure counter (`_consecutiveFailures`): on the
  `maxConsecutiveNotificationFailures`-th (ctor param, default 5) consecutive fault the client is
  auto-`UnregisterClient`-ed + a Warning logged; a successful delivery resets the counter (M1). The
  `HandleClientFailure(clientId)` hook is still called on every fault for derived-class customization
  (metrics, back-off) — it is **additional** to, not a replacement for, the base auto-unregister.
- `RegisterClient` uses `TryAdd` (not indexer-overwrite) and logs a Warning on a duplicate id before
  overwriting (L1). `Subscriptions` is a `ConcurrentBag<IDisposable>` so derived classes can register
  disposables from multiple threads (L2).
- `AbstractJsonRpcSession.OnWsPing` is `override` (not `new`): the current NetCoreServer
  `WsSession.OnWsPing` is virtual, so `new` would let the framework's internal ping dispatch bypass our
  handler. `WsSessionOnWsPingGuardTests` pins both halves (base virtual + we genuinely override) — a
  NetCoreServer bump that flips virtuality trips either the build (`override` won't compile) or the test.

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
