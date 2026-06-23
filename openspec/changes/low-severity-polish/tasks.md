# Tasks — low-severity-polish

## event-processor-resilience (M1 + L1 + L2)

- [x] `AbstractEventProcessor`: add optional ctor param `maxConsecutiveNotificationFailures = 5`
      (validate ≥ 1); add a `ConcurrentDictionary<Guid,int>` failure counter.
- [x] M1: in `NotifyClient`, on fault increment the counter, call `HandleClientFailure`, and once the
      threshold is reached `UnregisterClient` + log a Warning; on success reset the counter.
- [x] `UnregisterClient` also clears the failure counter for the client.
- [x] L1: `RegisterClient` uses `TryAdd`; on duplicate id log a Warning (then overwrite, preserving the
      effective last-wins behavior but no longer silent); reset the failure counter on (re)register.
- [x] L2: `Subscriptions` → `ConcurrentBag<IDisposable>`; update `TestEventProcessor.SubscriptionsAccessor`.
- [x] Add `[LoggerMessage]` methods for the two new Warning lines (block 1400s: 1406, 1407).
- [x] Tests: handler-always-throws → client auto-unregistered + warning; duplicate register → warning;
      `Subscriptions` type guard.

## api-polish (L3 + L4 + L6)

- [x] L6: `RpcErrorException` → `sealed`.
- [x] L4: clarify `SendNotificationAsync` / `SendBinaryDataAsync` XML docs (enqueue + return immediately).
- [x] L3 + L6 guards: reflection test — base `WsSession.OnWsPing` is virtual AND `AbstractJsonRpcSession`
      genuinely overrides it (the guard found the base had become virtual → fixed `new` → `override`);
      `RpcErrorException` is sealed.

## Wrap-up

- [x] `dotnet build` 0 warnings + `dotnet test` green (116 → expect ~120).
- [x] Doc-sync: `CLAUDE.md` (rules + version 2.2.0 + Implemented/planned + backlog + test count),
      `AUDIT-FINDINGS.md` (M1/L1–L7 shipped/closed), `.claude/rules/{audit-debt,patterns}.md`.
- [x] Version bump `Directory.Build.props` 2.1.0 → 2.2.0.
