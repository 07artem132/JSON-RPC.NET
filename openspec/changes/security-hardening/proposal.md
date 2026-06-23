# security-hardening — close the 4 critical audit findings (+ supply-chain) → 1.2.0

## Why

`AUDIT-FINDINGS.md` lists 4 HIGH findings (H1-H4). Three of them are genuine production/security risks
(DoS, lifecycle corruption, data race); H1 is an API-ergonomics gap, deferred. Separately, the transitive
dependency `StreamJsonRpc → MessagePack 2.5.192` carries a HIGH-severity advisory
(GHSA-hv8m-jj95-wg3x / NU1903) that currently can only be built past by disabling the NuGet audit
(`-p:NuGetAudit=false`) — i.e. we were masking a real vulnerability.

This change closes the genuinely critical items in one cluster, each with a regression-guard test
(per `.claude/rules/audit-debt.md` "fix without a guard is the next regression").

## What Changes

| # | Capability | Finding | Files | Risk |
|---|---|---|---|---|
| 1 | `dependency-vuln-messagepack` | (supply-chain) | `WsRpcServer.csproj` | none — restore-only |
| 2 | `parse-failure-throttle` | H2 (+ M9) | `WebSocketMessageHandler.cs`, `JsonRpcServerConfig.cs` | low |
| 3 | `dispose-cancellation` | H4 | `AbstractJsonRpcSession.cs`, `AbstractEventProcessor.cs`, `AbstractSubscriptionManager.cs` | medium |
| 4 | `service-registry-thread-safety` | H3 | `AbstractRpcServiceRegistry.cs` | low |

**Out of scope:** H1 (composition root), M2/M3/M4 (subscription API shape), M5 (config validation), AOT.
These are tracked separately; they are correctness/ergonomics debt, not ship-blocking risk.

## Capabilities

### `dependency-vuln-messagepack`
The build SHALL restore with the NuGet vulnerability audit **enabled** (no `-p:NuGetAudit=false`). The
transitive `MessagePack` SHALL be pinned to a patched version (≥ 2.5.198) via a direct `PackageReference`
in `WsRpcServer.csproj`, kept inside the StreamJsonRpc-compatible 2.5.x line.

### `parse-failure-throttle` (H2 + M9)
`WebSocketMessageHandler` SHALL bound malformed-JSON recovery: after `MaxConsecutiveParseFailures`
(new `JsonRpcServerConfig` option, default 10) consecutive `JsonException`s the connection SHALL be
closed with `WebSocketCloseStatus.ProtocolError` and the read loop ended. A successful deserialize SHALL
reset the counter. `CanRead`/`CanWrite` SHALL return `false` after `Dispose` (M9).

### `dispose-cancellation` (H4)
Types owning a `CancellationTokenSource` for background work SHALL signal cancellation **before** disposing
the CTS. `AbstractJsonRpcSession.Dispose` SHALL `Cts.Cancel()`, complete the notification channel, and
best-effort drain `NotificationProcessingTask` (bounded) before `Cts.Dispose()`. `AbstractEventProcessor`
SHALL `Cts.Cancel()` before `Cts.Dispose()`. `AbstractSubscriptionManager` SHALL best-effort drain
`OperationLock` before disposing it. All disposal paths SHALL be idempotent and swallow expected
cancellation/disposed exceptions.

### `service-registry-thread-safety` (H3)
`AbstractRpcServiceRegistry`'s service-type cache SHALL be built **exactly once** under concurrent
first-use (thread-safe init). When more than one implementation of the same RPC interface is discovered,
the registry SHALL log a Warning (no longer silently take the first). The reflection scan remains non-AOT
(documented; AOT alternative is a separate future change).

## Verification

- `dotnet build JSON-RPC.NET.sln -c Release` — 0 warnings, audit **on**.
- `dotnet test` — existing 83 pass + new guard tests (one per capability) green.
- Version bump `1.1.0 → 1.2.0` in `Directory.Build.props`; `AUDIT-FINDINGS.md` + `CLAUDE.md` move H2/H3/H4
  to a shipped state.
