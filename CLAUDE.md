# CLAUDE.md

Guidance for AI coding agents (Claude Code, Copilot, etc.) working in this repository.

> This layout mirrors the agent-instruction convention established in the sibling
> repo **SignalCli.NET** (the reference for agent-friendly practices in this org):
> a short always-relevant root file + path-scoped topic rules under `.claude/rules/`.

## Project

**WsRpcServer** (NuGet package id **`JSON-RPC.NET`**) — a high-performance, generic
WebSocket framework for bidirectional **JSON-RPC 2.0** services. It abstracts transport,
protocol and client-lifecycle so consumers only write business logic. Built on
[`NetCoreServer`](https://github.com/chronoxor/NetCoreServer) (transport) +
[`StreamJsonRpc`](https://github.com/microsoft/vs-streamjsonrpc) (protocol).

It is a **framework of abstract base classes** — consumers subclass `AbstractJsonRpcServer`,
`AbstractJsonRpcSession`, `AbstractEventProcessor`, `AbstractSubscriptionManager`,
`AbstractSubscriptionStore`, `AbstractRpcServiceRegistry` and register RPC services via
`[IRpcService]` discovery. The primary downstream consumer is **SignalCliNet.WsRpcServer**.

- Target framework: **net10.0**. Package version lives in `Directory.Build.props`
  (`<WsRpcServerPackageVersion>`, currently **2.8.0**) — never hardcode `<Version>` in a csproj.
- Five layers: Transport (WebSocket) → Protocol (JSON-RPC 2.0) → Session → Service → Subscription.
  Optional **secure** transport (TLS/mTLS) + RPC authorization layer over them (`secure-transport-mtls`, 2.6.0).
  Optional **observability** (`Meter`/`ActivitySource` "WsRpcServer") + concurrent-connection quota (`observability-and-resilience`, 2.7.0).

## Build & test

```bash
dotnet build JSON-RPC.NET.sln                                   # build all
dotnet test  tests/WsRpcServer.Tests/WsRpcServer.Tests.csproj   # run unit tests (~83)
dotnet test  tests/WsRpcServer.Tests/WsRpcServer.Tests.csproj --collect:"XPlat Code Coverage"
```

- This repo has **no private NuGet feed** — a plain `dotnet restore` works against nuget.org.
- `dotnet build` must be **0 warnings** in `src/WsRpcServer` and `tests/WsRpcServer.Tests`
  (both carry `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`). The `example/*` projects
  are intentionally lax (consumer-facing demos) and do not.
- Run tests after every meaningful change. If the test count drops or a new warning appears,
  stop and diagnose before moving on.

## Architecture (key types — all under `src/WsRpcServer/`)

- `Core/AbstractJsonRpcServer` — NetCoreServer `WsServer` subclass; accepts connections, creates sessions.
- `Sessions/AbstractJsonRpcSession` — per-connection lifecycle; owns the StreamJsonRpc instance + a `CancellationTokenSource`.
- `Transport/WebSocketMessageHandler` — `Stream` adapter feeding WS frames to StreamJsonRpc; framing + parse-recovery loop.
- `Services/AbstractRpcServiceRegistry` — discovery of `IRpcService` / `IClientAwareRpcService` implementations: prefers a source-generated `IRpcServiceCatalog` (AOT-safe), falls back to a reflection scan. The generator lives in `src/WsRpcServer.SourceGenerator` and ships inside the nupkg as an analyzer.
- `Events/AbstractEventProcessor` — fan-out of server→client notifications; per-client handler registry.
- `Subscriptions/AbstractSubscriptionManager` + `Core/AbstractSubscriptionStore` — subscription bookkeeping.
- `Core/JsonRpcServerConfig` — record holding `Host`/`Port`/`MaxMessageSizeBytes`/`NotificationQueueSize`.
- `Extensions/JsonRpcCoreExtensions` — `AddJsonRpcCore(...)` DI composition root.
- `Exceptions/RpcErrorException` — typed JSON-RPC error.

Patterns in use: Template Method (abstract base classes), Dependency Injection, Provider,
Adapter (`WebSocketMessageHandler : Stream`), Registry, Observer/notification fan-out.

## Topic-scoped rules

Path-scoped agent instructions live in `.claude/rules/` (load conditionally when editing matching files):

- [`.claude/rules/conventions.md`](.claude/rules/conventions.md) — modern C# / naming / comments-in-Ukrainian *(loads when editing `src/**`, `tests/**`, `example/**`)*.
- [`.claude/rules/patterns.md`](.claude/rules/patterns.md) — abstract-base-class framework patterns: DI composition root, disposal/cancellation, reflection registry thread-safety, subscription/event fan-out *(loads when editing `src/WsRpcServer/**`)*.
- [`.claude/rules/csproj-build.md`](.claude/rules/csproj-build.md) — `Directory.Build.props` canon + TreatWarningsAsErrors + single-source version + GitHub Actions SHA-pinning *(loads when editing `*.csproj`, `Directory.Build.props`, `.github/workflows/**`)*.
- [`.claude/rules/testing.md`](.claude/rules/testing.md) — xUnit / Moq conventions + no blocking `Task.WaitAll` in tests + regression-guard philosophy *(loads when editing `tests/**`)*.
- [`.claude/rules/openspec-workflow.md`](.claude/rules/openspec-workflow.md) — OpenSpec planning + `AUDIT-FINDINGS.md` as the backlog + commit-per-capability *(loads when editing `openspec/**`, `CLAUDE.md`)*.
- [`.claude/rules/audit-debt.md`](.claude/rules/audit-debt.md) — open audit findings + working style + prevention checklist *(always-load — cross-cutting)*.
- [`.claude/rules/cloud-dev.md`](.claude/rules/cloud-dev.md) — Claude Code on the web setup + SessionStart hook *(loads when editing `.claude/**`)*.

## Critical rules (do not regress)

1. **Single-source version.** The package version lives **only** in `Directory.Build.props`
   (`<WsRpcServerPackageVersion>`). `WsRpcServer.csproj` references it via `$(WsRpcServerPackageVersion)`.
   Never reintroduce a hardcoded `<Version>X.Y.Z</Version>`.
2. **Zero warnings = build failure.** `src/WsRpcServer` + `tests/WsRpcServer.Tests` opt into
   `TreatWarningsAsErrors`. Do not silence a real warning with a blanket `NoWarn`; fix it, or
   suppress narrowly with a justification comment. There are **no** repo-wide deferred suppressions left:
   `CA1848`/`CA1873` (LoggerMessage) shipped in `logger-message-migration` (2.1.0) — all library logging
   goes through source-generated `[LoggerMessage]` partials in `src/WsRpcServer/Logging/*Log.cs`, so a new
   ad-hoc `Logger.LogX("template", …)` in `src/WsRpcServer` now fails the build. Those two perf rules are
   suppressed **only** in the test csproj (ad-hoc test-double logging) and the example projects
   (`example/Directory.Build.props`, idiomatic consumer demos) — the same carve-out as `CA1707`.
3. **Disposal signals cancellation first.** Classes holding a `CancellationTokenSource` for
   background work (`AbstractJsonRpcSession`, `AbstractEventProcessor`) MUST `Cts.Cancel()` and
   drain the in-flight task **before** `Cts.Dispose()`; `AbstractSubscriptionManager` drains its
   `OperationLock` before disposing it. ✅ Shipped in `security-hardening` (1.2.0), guarded by tests.
4. **Reflection registry must be thread-safe + AOT-honest.** `AbstractRpcServiceRegistry`'s lazy
   type cache is built once via thread-safe init (volatile + double-checked `Lock`), and multiple
   implementations of one interface log a Warning instead of silent first-wins. ✅ Thread-safety
   shipped in `security-hardening` (1.2.0). The registry now **prefers a source-generated
   `IRpcServiceCatalog`** when one is injected (opt-in via `[assembly: GenerateRpcServiceCatalog]` +
   `AddGeneratedRpcServiceCatalog()`); the reflection scan is the fallback and is annotated
   `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]`. ✅ Source-gen discovery shipped in
   `registry-sourcegen-discovery` (2.3.0); `aot-readiness` (2.4.0) then annotated `AddJsonRpcCore<…>`'s
   generics (`[DynamicallyAccessedMembers]`) + suppressed the reflection-fallback boundary, so the
   **discovery path is provably Native-AOT-clean** (0 IL warnings under `-p:IsAotCompatible=true`; a real
   `PublishAot` native binary in `aot-smoke/` runs catalog-based discovery with no reflection).
   `aot-rpc-dispatch` (2.5.0) then made **dispatch** AOT-clean too: the generator also emits an
   `IRpcMethodBinder` (separate opt-in `AddGeneratedRpcMethodBinder()`) that registers each RPC-interface
   method via `JsonRpc.AddLocalRpcMethod(name, delegate)` (no AOT attributes) instead of the reflective
   `AddLocalRpcTarget`; `RegisterServices` prefers the binder, else falls back to the (annotated) reflection
   path. The `aot-smoke` native binary now binds dispatch via the generator with no reflection. ⚠ The
   binder is **not** 1:1 with `AddLocalRpcTarget` — it exposes only **interface** methods and drops target
   events / `RpcMarshalable` / `JsonRpcTargetOptions`; consumers needing those keep the reflection path
   (don't register the binder). **Still do not flip `<IsAotCompatible>true</IsAotCompatible>` on the
   library**: StreamJsonRpc's own formatter/envelope serialization is not AOT-clean in 2.25.29 (publish
   shows `IL3053`/`IL2104` on `StreamJsonRpc.dll` + transitive `Newtonsoft.Json`) — end-to-end AOT RPC
   payloads are an upstream gap, not ours. Honoring `[JsonRpcMethod]`/`[JsonRpcIgnore]` + camelCase is in
   the binder; unsupported method shapes (generic/ref/out/in/>16 params) emit `WSRPC002` and are skipped.
5. **No unbounded parse-failure loop.** The malformed-JSON recovery path in
   `WebSocketMessageHandler` is bounded: after `JsonRpcServerConfig.MaxConsecutiveParseFailures`
   (default 10) it closes the connection with `ProtocolError`. ✅ Shipped in `security-hardening` (1.2.0).
6. **Composition root stays in the library, not the consumer.** The generic
   `AddJsonRpcCore<TServer, TSession, TEventProcessor, TSubscriptionManager, TRegistry>(...)` registers
   all 5 core services + the concrete server (built from validated config) so consumers don't hand-wire
   them; registration is idempotent (sentinel marker + `TryAdd*`). ✅ Shipped in `composition-and-config`
   (1.3.0), guarded by `AddJsonRpcCoreCompositionTests`.
7. **Config is validated fail-fast.** `JsonRpcServerConfig` carries `[Range]`/`[Required]` DataAnnotations
   + a source-gen `[OptionsValidator]` (`JsonRpcServerConfigValidator`, reflection-free) wired through
   `AddJsonRpcCore`; invalid config throws `OptionsValidationException` on resolve. ✅ Shipped in
   `composition-and-config` (1.3.0), guarded by `JsonRpcServerConfigValidationTests`. Don't re-add
   `.ValidateDataAnnotations()` (reflection-based) alongside the source-gen validator.
8. **Subscription manager is generic + the base owns its lock.** `ISubscriptionManager<TEventType, TEventArgs>`
   is generic (no `object` params) and uses `topic` (not `account`). `AbstractSubscriptionManager<…>` makes
   the public `Subscribe`/`Unsubscribe`/`UpdateSubscription` template wrappers that run the abstract
   `*Core` methods under `OperationLock` via `WithLockAsync` — the base genuinely serializes mutations
   (M2). Derived classes override `*Core` and MUST NOT call the public `Subscribe` from inside a `*Core`
   (re-entrant `OperationLock` = deadlock). `GetClientsForEvent` stays lock-free (hot read path). ✅ Shipped
   in `subscription-manager-cleanup` (2.0.0), guarded by tests. (This is the breaking 2.0.0 wave.)
9. **Comments and log messages are written in Ukrainian** — match the surrounding code when editing.
10. **Library logging is source-generated.** Every `ILogger` call in `src/WsRpcServer` goes through a
    `[LoggerMessage]` partial method in `src/WsRpcServer/Logging/<Type>Log.cs` (one `internal static
    partial class` per type, EventId block reserved per type: server 1000s, session 1100s, transport
    1200s, registry 1300s, event-processor 1400s, **secure-transport 1500–1549, mTLS node-identity
    1550–1599, authorization 1600–1699**). No direct `Logger.LogX("template", …)` — `CA1848`/
    `CA1873` are active there. Add a new log line by adding a `[LoggerMessage]` method, not an inline call.
    ✅ Shipped in `logger-message-migration` (2.1.0), guarded by `LoggerMessageMigrationTests`.
11. **Secure transport is opt-in + additive; never blind-accept a certificate.** TLS/mTLS lives in the
    framework (it owns transport): `AbstractSecureJsonRpcServer : WssServer` + `AbstractSecureJsonRpcSession :
    WssSession` serve `wss://`; plaintext `AbstractJsonRpcServer`/`AbstractJsonRpcSession` are untouched
    (separate NetCoreServer hierarchies — the secure session duplicates the notification infra by necessity).
    Client-cert validation happens **inside** `RemoteCertificateValidationCallback` (NetCoreServer authenticates
    via `BeginAuthenticateAsServer`, so `CertificateChainPolicy` is unavailable): `CustomRootTrustValidator`
    builds its own `X509Chain` (`CustomRootTrust` + private CA `CustomTrustStore`, EKU `clientAuth`, default
    `RevocationMode.Offline`, optional SPKI-pin). **Never `=> true` in a cert callback and never
    `X509RevocationMode.NoCheck` without an adjacent `// justification:`** — `CertificateValidationConventionTests`
    fails the build otherwise. `[RpcAuthorize]` is **deny-by-default for attributed methods only** (un-attributed
    stay open): enforced on both dispatch paths via `RpcAuthorizationEnforcer.Enforce` (→ `RpcErrorException(-32001)`)
    — reflection path through `AuthorizingJsonRpc.DispatchRequestAsync`, generated binder emits the check at the
    delegate head (stays AOT-clean). ✅ Shipped in `secure-transport-mtls` (2.6.0), guarded by tests.

> Rules 3-5 shipped in `security-hardening` (1.2.0); rules 6-7 in `composition-and-config` (1.3.0);
> rule 8 in `subscription-manager-cleanup` (2.0.0); rule 10 in `logger-message-migration` (2.1.0);
> rule 11 in `secure-transport-mtls` (2.6.0) — each with a regression-guard test (see
> `tests/WsRpcServer.Tests/{Transport,Sessions,Events,Subscriptions,Services,Core,Extensions,Logging,Security,Authorization}`).
> When you fix a remaining finding, add its guard test and move its bullet to a shipped state.

## Maturity baseline (do not regress below this)

This repo is mid-maturation. The current floor, established by `foundation-cluster-1` (→ 1.1.0):

- **Build hygiene:** 0 warnings, `TreatWarningsAsErrors=true` on lib + tests; shared `Directory.Build.props`.
- **Tests:** unit suite green (**170**). Adding a feature that touches an open audit finding SHOULD add the matching regression-guard test (see `.claude/rules/audit-debt.md`).
- **Process:** non-trivial work goes through OpenSpec (`openspec/changes/<name>/`); `AUDIT-FINDINGS.md` is the prioritized backlog (4 HIGH / 9 MEDIUM / 7 LOW — **all now shipped/resolved**).

## Implemented / planned

- `foundation-cluster-1` (**1.1.0**) — build hygiene: `readme-org-fix`, `directory-build-props`, `warnings-cleanup` (439→0), `treat-warnings-errors`. See `openspec/changes/foundation-cluster-1/`.
- `security-hardening` (**1.2.0**) — the 4 critical items: `dependency-vuln-messagepack` (MessagePack advisory), `parse-failure-throttle` (H2 + M9), `dispose-cancellation` (H4), `service-registry-thread-safety` (H3). Each guarded by a test; build passes with NuGet audit on; suite 83 → 90. See `openspec/changes/security-hardening/`.
- `ci-bootstrap` — `.github/workflows/build.yml` (`CI`): NuGet vulnerability-audit gate + warnings-as-errors build + the 90-test suite on push/PR. Closes M8 + the broken README build badge (M7). No version bump (CI is not shippable).
- `composition-and-config` (**1.3.0**) — `config-validation` (M5: `JsonRpcServerConfig` DataAnnotations + source-gen `[OptionsValidator]` fail-fast) + `composition-root-complete` (H1: generic `AddJsonRpcCore<…>` registers all 5 services + concrete server + idempotency marker; consumer boilerplate removed from `example/SimpleServer/Program.cs`). Each guarded by a test; suite 90 → 112. See `openspec/changes/composition-and-config/`.
- `subscription-manager-cleanup` (**2.0.0**, BREAKING) — M2/M3/M4: `ISubscriptionManager` → generic `ISubscriptionManager<TEventType, TEventArgs>` (no `object`), `account` → `topic`, and `AbstractSubscriptionManager<…>` now serializes mutations through `OperationLock` (template methods over abstract `*Core`; M2). Generic `AddJsonRpcCore<…>` gains `TEventType, TEventArgs` (7 params). Suite 112 → 114. See `openspec/changes/subscription-manager-cleanup/`.
- `logger-message-migration` (**2.1.0**) — moved all ~51 `ILogger` call sites in `src/WsRpcServer` onto source-generated `[LoggerMessage]` partials (5 new `Logging/*Log.cs`, EventId block per type), removed the repo-wide `CA1848;CA1873` `<NoWarn>` (now active in the lib; suppressed only in test/example projects), added the `LoggerMessageMigrationTests` guard. Suite 114 → 116. See `openspec/changes/logger-message-migration/`.
- `low-severity-polish` (**2.2.0**) — M1 + the LOW band: M1 (`AbstractEventProcessor` auto-unregisters a client after N consecutive delivery failures, default 5, ctor-configurable; `HandleClientFailure` hook kept), L1 (`RegisterClient` → `TryAdd` + Warning on duplicate), L2 (`Subscriptions` → `ConcurrentBag`), L3 (`OnWsPing` `new` → `override` — NetCoreServer made the base virtual, so `new` silently bypassed our handler; guarded), L4 (enqueue-semantics XML docs), L6 (`RpcErrorException` sealed). L5/L7 already resolved/shipped. 5 new guard tests; suite 116 → 121. See `openspec/changes/low-severity-polish/`.
- `registry-sourcegen-discovery` (**2.3.0**) — rule #4 / H3 AOT follow-up: new `src/WsRpcServer.SourceGenerator` (Roslyn `IIncrementalGenerator`) emits a reflection-free `IRpcServiceCatalog` for any consumer that opts in with `[assembly: GenerateRpcServiceCatalog]` + `AddGeneratedRpcServiceCatalog()`. `AbstractRpcServiceRegistry` prefers an injected catalog; reflection scan kept as `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]` fallback. Generator packed into the nupkg (`analyzers/dotnet/cs`). 4 new guard tests (generator-driver + runtime catalog); suite 121 → 125. **Does not** flip `IsAotCompatible` — StreamJsonRpc's `AddLocalRpcTarget` is the remaining external blocker. See `openspec/changes/registry-sourcegen-discovery/`.
- `aot-readiness` (**2.4.0**) — made the source-gen discovery path **provably Native-AOT-clean**: `[DynamicallyAccessedMembers(PublicConstructors)]` on `AddJsonRpcCore<…>`'s 5 service generics (clears IL2091) + honest `[UnconditionalSuppressMessage]` at the reflection-fallback boundary (IL2026/IL3050/IL3000, justified by "catalog is the AOT path"). Result: **0 IL warnings** under `-p:IsAotCompatible=true`, and a real `dotnet publish -p:PublishAot=true` native binary (`aot-smoke/`) runs catalog-based discovery with no reflection (exit 0). Does **not** flip `IsAotCompatible` (RPC dispatch still uses StreamJsonRpc reflection — see rule #4). See `openspec/changes/aot-readiness/`.
- `aot-rpc-dispatch` (**2.5.0**) — made RPC **dispatch** Native-AOT-clean: the generator also emits an `IRpcMethodBinder` (separate opt-in `AddGeneratedRpcMethodBinder()`) that registers each RPC-interface method via source-generated `JsonRpc.AddLocalRpcMethod(name, delegate)` (no AOT attributes) instead of the reflective `AddLocalRpcTarget`; `RegisterServices` prefers the binder, else falls back to the annotated reflection path. Honors `[JsonRpcMethod]`/`[JsonRpcIgnore]` + camelCase; unsupported method shapes → `WSRPC002`, skipped. `aot-smoke` native binary binds dispatch via the generator (exit 0, no reflection). 3 new guard tests; suite 125 → 128. ⚠ Behavior trade-off: binder exposes only interface methods, drops target events / RpcMarshalable / JsonRpcTargetOptions (reflection path kept for those). Does **not** flip `IsAotCompatible` — StreamJsonRpc payload serialization (2.21.69) is the upstream gap (IL3053 on StreamJsonRpc.dll). See `openspec/changes/aot-rpc-dispatch/`.
- `secure-transport-mtls` (**2.6.0**) — authn/authz track (README roadmap "Авторизація та аутентифікація"):
  3 capabilities. **`tls-transport`**: `AbstractSecureJsonRpcServer : WssServer` + `AbstractSecureJsonRpcSession :
  WssSession` serve `wss://` from validated `TlsServerOptions` (source-gen `[OptionsValidator]` fail-fast;
  `SecureTransport.Create` builds the `SslContext` once); plaintext path untouched. **`mtls-node-identity`**:
  `ClientCertificateRequired=true` + pluggable `INodeCertificateValidator` (default `CustomRootTrustValidator` —
  manual `X509Chain`, `CustomRootTrust` + private CA, EKU `clientAuth`, `RevocationMode.Offline`, optional SPKI-pin)
  validated inside `RemoteCertificateValidationCallback`; validated cert → `NodeIdentity` (`INodeIdentityResolver`,
  default SAN-URI/SPIFFE with SPKI fallback) → `ClaimsPrincipal` on the session (correlated per-connection via
  `SecureTransport`'s `ConditionalWeakTable` keyed by `SslStream` — one cached reflection field-read of NetCoreServer's
  private `_sslStream`, `SslSessionInterop`). **`rpc-authorization`**: `[RpcAuthorize(Roles=…)]` deny-by-default for
  attributed methods on both dispatch paths (`AuthorizingJsonRpc.DispatchRequestAsync` reflection + generator-emitted
  check at the binder delegate head) via `RpcAuthorizationEnforcer` → `RpcErrorException(-32001)`; default
  `StaticRoleMapAuthorizationPolicy` (identity→roles map). `IRpcMethodBinder.Bind` gained a `ClaimsPrincipal?` param;
  `IRpcServiceRegistry` gained a principal-aware `RegisterServices` (DIM default → 2-arg). New EventId blocks
  1500–1699. No new NuGet dep. 24 new guard tests (suite 134 → 158), incl. the `CertificateValidationConventionTests`
  Roslyn guard (no `=> true` / unjustified `NoCheck`). Does **not** flip `IsAotCompatible` (rule #4 upstream blocker
  stands). **Downstream follow-up (separate change):** `SignalCliNet.WsRpcServer` wires cert + private CA + SPKI +
  node→roles via `AddSignalJsonRpc`. See `openspec/changes/secure-transport-mtls/`.
- `observability-and-resilience` (**2.7.0**) — closes the README-roadmap "Аналітика та моніторинг" item +
  adds a connection quota. 2 capabilities. **`observability`**: `WsRpcServerDiagnostics` exposes a `Meter`
  + `ActivitySource` named `"WsRpcServer"` (instruments `wsrpc.connections.active`/`.rejected`,
  `wsrpc.notifications{result}`, `wsrpc.parse_failures`, `wsrpc.authorization.denied`; per-connection span)
  instrumented at 4 framework-owned seams (server `OnConnected`/`OnDisconnected`, session
  `SendNotificationAsync`, transport parse-recovery, `RpcAuthorizationEnforcer`). **Privacy is an invariant**
  (mirror SignalCli.NET): tag keys ⊆ `AllowedTagKeys` (`{ "result" }`), never message/identity payloads —
  pinned by `WsRpcServerDiagnosticsTests` (`MeterListener`). Inert without listeners. **`connection-resilience`**:
  `JsonRpcServerConfig.MaxConcurrentConnections` (`[Range(0,…)]`, default `0` = unlimited) enforced in
  `AbstractJsonRpcServer.OnConnected` (TCP-accept seam) — over-limit → Warning (EventId 1001) +
  `connections.rejected` + `Disconnect`, before RPC dispatch; guarded by a real loopback `ConnectionQuotaTests`.
  No new NuGet dep. Suite 158 → 164. **Deferred (separate changes):** idle-timeout (consumer-owned
  `OnWsReceived` seam), graceful drain (NetCoreServer `Stop()` is synchronous), per-`NodeIdentity` limits,
  a separate `JSON-RPC.NET.HealthChecks` package. See `openspec/changes/observability-and-resilience/`.
- `browser-ws-interop` (**2.8.0**) — closes two WHATWG-browser-interop backlog items found by the
  `SignalCliNet.WsRpcServer` consumer, both **additive / opt-in (default = current wire behavior)**.
  **Subprotocol echo:** `AbstractJsonRpcSession` (+ secure `AbstractSecureJsonRpcSession`, rule #11) gain
  `protected virtual string? NegotiateSubprotocol(IReadOnlyList<string> offered)` (default `null`) + an
  `override bool OnWsConnecting(HttpRequest, HttpResponse)` that parses `Sec-WebSocket-Protocol` and, when
  the hook returns non-null, **fully rebuilds** the 101 response with the header among the headers
  (RFC 6455 `Base64(SHA1(key+GUID))`) via internal `WsUpgradeInterop` — encapsulating the workaround for
  NetCoreServer 8.0.7's `OnWsConnecting`-after-`SetBody()` defect that otherwise leaks a stray frame; a null
  hook returns `base.OnWsConnecting` (unchanged). **Text frames:** `JsonRpcServerConfig.UseTextFramesForOutgoingMessages`
  (bool, default `false` = Binary) + `IJsonRpcSession.SendTextDataAsync` (both base sessions, via NetCoreServer
  `SendTextAsync`); `WebSocketMessageHandler.WriteCoreAsync` routes by the flag. New session-block EventIds
  1117–1120. No new NuGet dep. Suite 164 → 170 (2 subprotocol loopback + 4 text-frame guards). See
  `openspec/changes/browser-ws-interop/`.
- **Backlog** (from `AUDIT-FINDINGS.md`): **empty** — all 20 findings shipped/resolved, plus the AOT track (`registry-sourcegen-discovery` → `aot-readiness` → `aot-rpc-dispatch`) is complete for the part we own (discovery + dispatch). The remaining AOT limit is **upstream**: StreamJsonRpc 2.25.29's formatter/envelope serialization isn't AOT-clean (IL3053), so `<IsAotCompatible>true</IsAotCompatible>` stays off until StreamJsonRpc ships an AOT-safe formatter. The StreamJsonRpc-replacement question was researched (spike) and rejected.

## Known upstream limitations

Diagnosed, deliberately **not fixed** — the root cause is outside our code and any workaround would be
risky or ineffective. Do not "fix" these by hacking the transport; they are documented won't-do's.

- **WS control frames (ping) are serialized behind the single per-connection receive pump — NetCoreServer
  8.0.7, not us.** NetCoreServer runs the *entire* inbound path on one sequential receive pump per
  connection: `TcpSession.OnAsyncCompleted` → `ProcessReceive` (calls `OnReceived`, then re-arms the next
  socket read only *after* it returns) → `TryReceive` (decompiled `TcpSession.cs`, `OnAsyncCompleted`
  ≈L520-543 / `ProcessReceive` ≈L548-586 / `TryReceive` ≈L428-451). `WsSession.OnReceived` forwards to
  `WebSocket.PrepareReceiveFrame` (`WsSession.cs` ≈L435-445), which parses frames in a `while (size > 0)`
  loop under `lock (WsReceiveLock)` and dispatches each opcode **inline** — ping(9)→`OnWsPing`,
  data(1/2)→`OnWsReceived` (`WebSocket.cs` ≈L357-530, dispatch switch ≈L503-527). The auto-pong is produced
  **inline on that pump thread**: `WsSession.OnWsPing` → `SendPongAsync` (`WsSession.cs` ≈L515-518). So a
  client ping is answered only once the pump reaches the ping frame — there is **no separate control-frame
  thread**. (Verified against the installed 8.0.7 DLL via `ilspycmd`; there is no matching `8.0.7` git tag —
  it's a NuGet-only build past the `8.0.4` tag.)
- **Why it is nevertheless NOT our transport holding the pump.** Our inbound path is already decoupled: the
  session's `OnWsReceived` is fire-and-forget into a `System.IO.Pipelines.Pipe`
  (`WebSocketMessageHandler.ProcessReceivedDataAsync` just `_writer.Write(span)` + discards `FlushAsync()`,
  `WebSocketMessageHandler.cs:81-108`) and returns immediately; StreamJsonRpc reads + dispatches the RPC on a
  **decoupled thread-pool loop** (the `Pipe`'s default `ReaderScheduler` is `PipeScheduler.ThreadPool`), never
  on the pump thread. So a long-*processing* (properly async) in-flight RPC does **not** hold the pump, and a
  ping arriving during it *is* parsed and *is* ponged. The only genuine pump-occupancy is a single very large
  **inbound** frame (accumulated across many `OnReceived` calls before dispatch) — bounded by transfer time
  and inherent to WebSocket framing (a control frame can't interleave a non-fragmented data frame on the wire),
  not RPC processing time.
- **What actually delays the pong** is therefore **thread-pool starvation** in the consumer (sync-over-async /
  blocking RPC handlers occupy pool threads, so the socket-receive completion can't be scheduled to parse the
  ping) — a consumer/runtime concern, not a framework defect. A server-initiated keepalive
  (`WsSession.SendPingAsync` on a timer) would **not** help a `python-websockets` client: its `ping_timeout`
  waits for a **pong to its own ping**, and an inbound server ping doesn't reset that timer — so a keepalive
  thread is complexity without the payoff. **Mitigation** (already in the `SignalCliNet.WsRpcServer` consumer):
  app-level heartbeat, and clients set `ping_interval=None`. No framework change, no version bump.

## Git

Work on a feature branch; do not push or commit unless asked. Prefer **one commit per OpenSpec
capability/cluster**. Never force-push or amend already-pushed commits without explicit approval.
