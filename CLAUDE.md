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
  (`<WsRpcServerPackageVersion>`, currently **1.2.0**) — never hardcode `<Version>` in a csproj.
- Five layers: Transport (WebSocket) → Protocol (JSON-RPC 2.0) → Session → Service → Subscription.

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
- `Services/AbstractRpcServiceRegistry` — reflection-based discovery of `IRpcService` / `IClientAwareRpcService` implementations.
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
   suppress narrowly with a justification comment. `CA1848`/`CA1873` (LoggerMessage) are the only
   repo-wide deferred suppressions — see `Directory.Build.props` and the `logger-message-migration`
   future capability.
3. **Disposal signals cancellation first.** Classes holding a `CancellationTokenSource` for
   background work (`AbstractJsonRpcSession`, `AbstractEventProcessor`) MUST `Cts.Cancel()` and
   drain the in-flight task **before** `Cts.Dispose()`; `AbstractSubscriptionManager` drains its
   `OperationLock` before disposing it. ✅ Shipped in `security-hardening` (1.2.0), guarded by tests.
4. **Reflection registry must be thread-safe + AOT-honest.** `AbstractRpcServiceRegistry`'s lazy
   type cache is built once via thread-safe init (volatile + double-checked `Lock`), and multiple
   implementations of one interface log a Warning instead of silent first-wins. ✅ Thread-safety
   shipped in `security-hardening` (1.2.0). The reflection scan is **not AOT-compatible** — do not
   set `<IsAotCompatible>true</IsAotCompatible>` without a source-gen alternative (AOT still open).
5. **No unbounded parse-failure loop.** The malformed-JSON recovery path in
   `WebSocketMessageHandler` is bounded: after `JsonRpcServerConfig.MaxConsecutiveParseFailures`
   (default 10) it closes the connection with `ProtocolError`. ✅ Shipped in `security-hardening` (1.2.0).
6. **Composition root stays in the library, not the consumer.** `AddJsonRpcCore` should register
   the core services + concrete server so consumers don't hand-wire 5 services. Open finding **H1**.
7. **Comments and log messages are written in Ukrainian** — match the surrounding code when editing.

> Rules 3-5 shipped in `security-hardening` (1.2.0) — each with a regression-guard test (see
> `tests/WsRpcServer.Tests/{Transport,Sessions,Events,Subscriptions,Services}`). Rule 6 (H1) is still
> open. When you fix a remaining finding, add its guard test and move its bullet to a shipped state.

## Maturity baseline (do not regress below this)

This repo is mid-maturation. The current floor, established by `foundation-cluster-1` (→ 1.1.0):

- **Build hygiene:** 0 warnings, `TreatWarningsAsErrors=true` on lib + tests; shared `Directory.Build.props`.
- **Tests:** unit suite green (**90**). Adding a feature that touches an open audit finding SHOULD add the matching regression-guard test (see `.claude/rules/audit-debt.md`).
- **Process:** non-trivial work goes through OpenSpec (`openspec/changes/<name>/`); `AUDIT-FINDINGS.md` is the prioritized backlog (4 HIGH / 9 MEDIUM / 7 LOW).

## Implemented / planned

- `foundation-cluster-1` (**1.1.0**) — build hygiene: `readme-org-fix`, `directory-build-props`, `warnings-cleanup` (439→0), `treat-warnings-errors`. See `openspec/changes/foundation-cluster-1/`.
- `security-hardening` (**1.2.0**) — the 4 critical items: `dependency-vuln-messagepack` (MessagePack advisory), `parse-failure-throttle` (H2 + M9), `dispose-cancellation` (H4), `service-registry-thread-safety` (H3). Each guarded by a test; build passes with NuGet audit on; suite 83 → 90. See `openspec/changes/security-hardening/`.
- **Backlog** (from `AUDIT-FINDINGS.md`, ordered low-risk first): `config-validation` (M5) → `composition-root-complete` (H1) → `subscription-manager-cleanup` (M2/M3/M4) → `ci-bootstrap` (M8) → `logger-message-migration` → registry AOT source-gen alternative.

## Git

Work on a feature branch; do not push or commit unless asked. Prefer **one commit per OpenSpec
capability/cluster**. Never force-push or amend already-pushed commits without explicit approval.
