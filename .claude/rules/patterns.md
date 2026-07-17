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

## Secure transport + authorization (`secure-transport-mtls`, 2.6.0)

- ✅ Shipped. TLS/mTLS is a **transport-layer mechanism** so it lives in the framework, opt-in + additive
  (CLAUDE.md rule #11). `AbstractSecureJsonRpcServer : WssServer` + `AbstractSecureJsonRpcSession : WssSession`
  serve `wss://`; the plaintext `AbstractJsonRpcServer`/`AbstractJsonRpcSession` are untouched. NetCoreServer
  splits plaintext (`WsSession : HttpSession`) and TLS (`WssSession : HttpsSession`) into **separate hierarchies
  with no shared WS base type**, so the secure session **duplicates** the notification infra (channel /
  `SendNotificationAsync` / `ProcessNotificationsAsync` / disposal) — that divergence is unavoidable; keep both
  in sync if you touch one.
- **Validation runs inside `RemoteCertificateValidationCallback`.** NetCoreServer authenticates via
  `BeginAuthenticateAsServer` (APM `SslStream`), so the modern `SslServerAuthenticationOptions.CertificateChainPolicy`
  is **not** available — you cannot hand the runtime an `X509ChainPolicy` declaratively. `CustomRootTrustValidator`
  builds its own `X509Chain` (`TrustMode = CustomRootTrust` + private-CA `CustomTrustStore` — never the machine
  store; EKU `clientAuth`; `RevocationMode.Offline` by default; optional SPKI-SHA-256 pin). **Never `=> true` in a
  cert callback; never `X509RevocationMode.NoCheck` without an adjacent `// justification:`** — the Roslyn guard
  `CertificateValidationConventionTests` fails the build (the "ходьба по колу" guard class; it's Roslyn member-access
  based, so doc-comment `<see cref>` mentions don't trip it).
- **Per-connection identity correlation.** The single shared callback's `sender` is the session's `SslStream`;
  `SecureTransport` stores the validated `NodeIdentity` in a `ConditionalWeakTable<object, …>` keyed by that stream,
  and the secure session retrieves it in `OnWsConnected` via `SslSessionInterop` — **one cached reflection read** of
  NetCoreServer's private `SslSession._sslStream` (justified transport glue, `[UnconditionalSuppressMessage]`, fails
  closed if the field is renamed). `INodeIdentityResolver` (default `SpiffeNodeIdentityResolver`: SAN-URI/SPIFFE name,
  SPKI-SHA-256 fallback; URI SANs parsed via `AsnReader` since the BCL SAN extension only enumerates DNS/IP) →
  `NodeIdentityPrincipalFactory.Create` → `ClaimsPrincipal` (authType `"mtls"` ⇒ `IsAuthenticated`).
- **Authorization is deny-by-default for attributed methods only.** `[RpcAuthorize(Roles=…)]` (method or interface).
  `RpcAuthorizationEnforcer.Enforce` is the **single** primitive (throws `RpcErrorException(-32001)`, fails closed
  when policy is null). Two dispatch call sites: reflection path via `AuthorizingJsonRpc.DispatchRequestAsync`
  (consumer constructs `new AuthorizingJsonRpc(handler, principal, policy)` instead of `new JsonRpc(handler)`;
  reads `[RpcAuthorize]` through `RpcAuthorizationMetadata` over the interface map); generated binder emits the
  `Enforce(...)` call **at the head of the delegate** for attributed methods (attribute read at compile time → stays
  AOT-clean). `IRpcMethodBinder.Bind` gained a `ClaimsPrincipal?` param; `IRpcServiceRegistry.RegisterServices` has a
  principal-aware overload (DIM default forwards to the 2-arg). Default policy `StaticRoleMapAuthorizationPolicy`
  (identity→roles map). Un-attributed methods stay open — purely additive.
- DI: `AddSecureJsonRpcCore<…>(Action<JsonRpcServerConfig>?, Action<TlsServerOptions>?)` + `AddSecureTransport(...)`
  mirror the plaintext composition root; `TlsServerOptions` is validated fail-fast (source-gen `[OptionsValidator]`
  `TlsServerOptionsValidator` + cross-field rules: server cert has a private key, mTLS needs ≥1 `TrustedRoots`).
  Logging EventId blocks **1500–1549** (secure server), **1550–1599** (mTLS validator/identity), **1600–1699**
  (authorization). Full reference: [`docs/api/security.md`](../../docs/api/security.md).

## Observability + connection quota (`observability-and-resilience`, 2.7.0)

- ✅ Shipped. `WsRpcServerDiagnostics` (`src/WsRpcServer/Diagnostics/`) owns a static `Meter` +
  `ActivitySource` named `"WsRpcServer"`. Instruments: `wsrpc.connections.active` (UpDownCounter),
  `wsrpc.connections.rejected`, `wsrpc.notifications{result=queued|dropped}`, `wsrpc.parse_failures`,
  `wsrpc.authorization.denied`; a per-connection span. **Add a metric via a helper on `WsRpcServerDiagnostics`,
  not an inline `Meter.Create*` at the call site** — keeps the privacy allowlist enforceable in one place.
- **Privacy is an invariant** (mirror SignalCli.NET rule #1): measurement tag keys MUST stay within
  `WsRpcServerDiagnostics.AllowedTagKeys` (`{ "result" }`) — only enum-like literals / statuses / counts,
  never message bodies, phones, or identity secrets. `WsRpcServerDiagnosticsTests` pins this with a
  `MeterListener` that asserts every captured tag key is in the allowlist; a new tag key without updating
  the allowlist + test fails loudly. Instrumentation is **inert without listeners** (no behavior/perf impact).
- **Instrument at framework-owned seams only** (not consumer-owned overrides): server
  `OnConnected`/`OnDisconnected` (`TcpServer` virtuals — the consumer overrides session-level `OnWsConnected`,
  so these don't collide), session `SendNotificationAsync`, transport parse-recovery loop, and
  `RpcAuthorizationEnforcer`. The active-connection gauge balances inc/dec via a
  `ConditionalWeakTable<TcpSession, …>` marker so only **accepted** connections count (rejected ones don't).
- **Connection quota.** `JsonRpcServerConfig.MaxConcurrentConnections` (`[Range(0,…)]`, default `0` = unlimited)
  is enforced in `AbstractJsonRpcServer.OnConnected`: when `> 0` and `ConnectedSessions` exceeds it →
  Warning (EventId **1001**, server block) + `connections.rejected` + `session.Disconnect()` before RPC
  dispatch. Resolved lazily from DI (`Lazy<int>` over `ServiceProvider`); `0` if no config (server without DI).
  Guarded by a real loopback `ConnectionQuotaTests` (two `ClientWebSocket`s, polled — no fixed sleeps).
- **Deferred (documented in the proposal, not gaps):** idle-timeout needs a consumer-cooperative API
  (`OnWsReceived` is consumer-owned and often doesn't call `base`); graceful drain needs an accept-gate
  (NetCoreServer `Stop()` is synchronous and tears down everything); per-`NodeIdentity` limits build on this
  quota + the authz layer; a separate `JSON-RPC.NET.HealthChecks` package is a later iteration. Full
  reference: [`docs/api/observability.md`](../../docs/api/observability.md).

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

## Browser (WHATWG) WebSocket interop (`browser-ws-interop`, 2.8.0)

- ✅ Shipped. Two **additive / opt-in** seams (default = current wire behavior — the single consumer must
  not silently change framing). Both live in the sessions because the framework owns transport, and both are
  **duplicated across the plaintext + secure hierarchies** (rule #11 — no shared WS base type); the pure,
  transport-agnostic logic (parse offered subprotocols + rebuild the 101) is factored into internal
  `Sessions/WsUpgradeInterop.cs` so only the thin `OnWsConnecting` override is duplicated.
- **Subprotocol echo.** `protected virtual string? NegotiateSubprotocol(IReadOnlyList<string> offered)`
  (default `null`) is the consumer hook. The base `override bool OnWsConnecting(HttpRequest, HttpResponse)`
  parses `Sec-WebSocket-Protocol` (multiple headers OR comma-list), calls the hook, and — when it returns
  non-null — **fully rebuilds** the 101 response (`Clear` → `SetBegin(101)` → `Connection`/`Upgrade`/
  `Sec-WebSocket-Accept` = `Base64(SHA1(key+GUID))` + `Sec-WebSocket-Protocol` → `SetBody`). This encapsulates
  the workaround for **NetCoreServer 8.0.7**, where `PerformServerUpgrade` calls `OnWsConnecting` **after**
  `response.SetBody()` (verified from upstream: the `OnWsConnecting`-before-`SetBody` fix is master commit
  `2c1cdc47`, post-8.0.7) — so a naive `response.SetHeader(...)` in an override appends **after** the
  body-separator and leaks into the WS stream as a garbage frame (WHATWG client sees "reserved bits set",
  never reaches open). A null hook returns `base.OnWsConnecting(...)` — behavior unchanged. The SHA-1 is
  RFC 6455 handshake glue, not security crypto (narrow `CA5350` suppression with a `justification:` comment).
- **Text frames.** `JsonRpcServerConfig.UseTextFramesForOutgoingMessages` (bool, default `false` = Binary,
  no `[Range]`). When `true`, `WebSocketMessageHandler.WriteCoreAsync` sends via `IJsonRpcSession.SendTextDataAsync`
  (both base sessions, over NetCoreServer `SendTextAsync`) instead of `SendBinaryDataAsync` — WHATWG browsers
  expect a Text frame for JSON (`event.data` is a `string`, not a `Blob`). `SendTextDataAsync` mirrors
  `SendBinaryDataAsync` exactly (dispose-guard + log). New session-block EventIds 1117–1120.
- **Guards:** real loopback `SubprotocolNegotiationTests` (`ClientWebSocket` offers a subprotocol → server
  echoes exactly it, reaches open, first post-upgrade frame is valid) + default-hook-unchanged; `OutgoingFrameTypeTests`
  (loopback Text/Binary per flag through the real `WriteCoreAsync`, plus a deterministic handler-level routing test).

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
