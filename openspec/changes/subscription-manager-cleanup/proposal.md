# subscription-manager-cleanup — type-safe subscription API + base owns its lock → 2.0.0

## Why

`AUDIT-FINDINGS.md` clusters three MEDIUM findings on the subscription base, all API foot-guns:

- **M2** — `AbstractSubscriptionManager.OperationLock` is declared (and drained on dispose per critical
  rule #3) but the base **never uses it**. A consumer sees a semaphore on the base and reasonably assumes
  the base coordinates mutations — it doesn't, so derived classes silently race.
- **M3** — `Subscribe(Guid, string account, …)` leaks the domain-specific name `account` into a generic
  WebSocket-JSON-RPC framework.
- **M4** — `eventTypes`/`args`/`eventType` are typed `object`, so all type safety is lost; consumers cast
  at runtime.

These are a single coherent redesign of one type, so they ship together. This is a **breaking** change to
the published public API (`ISubscriptionManager` becomes generic; `AddJsonRpcCore<…>` gains two type
parameters), hence the **major** version bump.

## What Changes

| Finding | Change |
|---|---|
| M4 | `ISubscriptionManager` → `ISubscriptionManager<TEventType, TEventArgs>`; `object` params become `IReadOnlyCollection<TEventType>` (subscribe/update) and `TEventArgs`/`TEventType` (`GetClientsForEvent`). |
| M3 | `Subscribe`'s `account` parameter renamed to `topic`. |
| M2 | `AbstractSubscriptionManager<…>` now *uses* `OperationLock`: the public `Subscribe`/`Unsubscribe`/`UpdateSubscription` are template wrappers that run new abstract `*Core` methods under the lock via `WithLockAsync`. `GetClientsForEvent` stays lock-free (hot read path). |

Knock-on (same change): the generic `AddJsonRpcCore<TServer, TSession, TEventProcessor, TSubscriptionManager, TRegistry>`
gains `TEventType, TEventArgs` (now 7 params, constrained `TSubscriptionManager : ISubscriptionManager<TEventType, TEventArgs>`)
and registers the generic interface. `example/SimpleServer` (`DemoSubscriptionManager`, `DemoEventsRpcAdapter`,
`IDemoEventsRpc`, `Program.cs`) updated to the new shape.

**Why the audit's suggested `ISubscriptionManager<TEventType, TArgs>` signature was adjusted:** `Subscribe`
takes a *collection* of event types while `GetClientsForEvent` matches a *single* one, so a single
`TEventType` can't serve both as written — `Subscribe`/`UpdateSubscription` therefore take
`IReadOnlyCollection<TEventType>`, mirroring the already-generic `AbstractSubscriptionStore<TSubscription, TEventArgs, TEventType>`.

**Why M2 keeps the lock rather than removing it:** the "just delete the unused lock" option would
contradict shipped critical rule #3 (drain `OperationLock` before dispose, guarded by a test). Making the
base *use* the lock resolves the foot-gun while preserving that invariant.

## Capabilities

### `subscription-manager-cleanup` (M2 + M3 + M4)
`ISubscriptionManager<TEventType, TEventArgs>` SHALL be generic and `object`-free, with `topic` (not
`account`). `AbstractSubscriptionManager<TEventType, TEventArgs>` SHALL serialize the three mutating
operations through `OperationLock` (template method + abstract `*Core`), expose `WithLockAsync`, throw
`ObjectDisposedException` from a mutating call after dispose, and keep `GetClientsForEvent` lock-free. The
generic `AddJsonRpcCore` overload SHALL register the generic interface to the same singleton instance as
the concrete manager.

## Verification

- `dotnet build JSON-RPC.NET.sln -c Release` — 0 warnings, NuGet audit on.
- `dotnet test` — suite green (112 → 114): adds an M2 lock-serialization guard + a post-dispose
  typed-error guard.
- Version bump `1.3.0 → 2.0.0` (breaking); `AUDIT-FINDINGS.md` + `CLAUDE.md` move M2/M3/M4 to shipped.
