# low-severity-polish — close M1 + remaining LOW findings (L1–L7) → 2.2.0

## Why

After the HIGH/MEDIUM waves, `AUDIT-FINDINGS.md` leaves one MEDIUM (M1) and the LOW band (L1–L7) open,
plus the open-ended registry-AOT item (tracked separately — it needs a source generator and is not part
of this cluster). M1 is a real robustness gap: a broken client whose notification handler keeps faulting
is never removed, so the fan-out logs an error per event forever. The LOW items are API foot-guns and
hygiene nits. None is ship-blocking, but each is a small, durable fix + guard.

## What Changes

| Finding | Fix | Files |
|---|---|---|
| **M1** | `AbstractEventProcessor` gains a built-in consecutive-failure counter (default 5, ctor-configurable) that auto-unregisters a persistently-failing client + logs a Warning. The `HandleClientFailure` hook stays for custom logic; success resets the counter. | `AbstractEventProcessor.cs` |
| **L1** | `RegisterClient` uses `TryAdd` and logs a Warning on duplicate registration instead of overwriting silently. | `AbstractEventProcessor.cs` |
| **L2** | `Subscriptions` becomes a thread-safe `ConcurrentBag<IDisposable>` (was `List<IDisposable>`). | `AbstractEventProcessor.cs` |
| **L3** | A reflection guard pins `NetCoreServer.WsSession.OnWsPing` as non-virtual, so a dependency bump that makes it `virtual` (invalidating the `new` keyword) trips the build. | test only |
| **L4** | `SendNotificationAsync` / `SendBinaryDataAsync` XML docs clarify they **enqueue and return immediately** (don't await actual delivery). Names kept — a rename would be a needless break. | `AbstractJsonRpcSession.cs` |
| **L6** | `RpcErrorException` sealed (typed-exception-leaf pattern, mirrors SignalCli.NET). | `RpcErrorException.cs` |
| L5 | Already resolved — `JsonRpcCoreExtensions` was rewritten in `composition-and-config`; no stray blank line. | — |
| L7 | Already shipped — this repo now has `CLAUDE.md` + `.claude/rules/` + `openspec/`. | — |

**Out of scope:** registry AOT source-gen discovery alternative (rule #4) — open-ended, needs a source
generator and possibly a public-API change; tracked as the lone remaining backlog item.

### Compatibility
Minor bump (2.2.0). The M1 counter is additive default behavior (configurable off via a high threshold).
Two borderline source-level changes, both harmless for the known downstream (`SignalCliNet.WsRpcServer`
uses only `Subscriptions.Add(...)` and does not derive `RpcErrorException`): `Subscriptions` type
`List` → `ConcurrentBag` (only `.Add`/`.Clear`/enumeration are part of the contract) and sealing
`RpcErrorException` (no derived types exist in any sibling repo).

## Capabilities

### `event-processor-resilience` (M1 + L1 + L2)
`AbstractEventProcessor` SHALL track consecutive notification-delivery failures per client and, on
reaching `maxConsecutiveNotificationFailures` (ctor param, default 5), SHALL auto-`UnregisterClient` and
log a Warning; a successful delivery SHALL reset that client's counter. `RegisterClient` SHALL use
`TryAdd` and log a Warning (not overwrite silently) on a duplicate id. `Subscriptions` SHALL be a
thread-safe collection. Guarded by behavioral tests (handler-always-throws → auto-unregister + warning;
duplicate register → warning) and a type guard on `Subscriptions`.

### `api-polish` (L3 + L4 + L6)
`RpcErrorException` SHALL be `sealed`. The enqueue-style session methods SHALL document their
fire-and-forget semantics. A reflection guard SHALL pin `WsSession.OnWsPing` non-virtual and
`RpcErrorException` sealed.
