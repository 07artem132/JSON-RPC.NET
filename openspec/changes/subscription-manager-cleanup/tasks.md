# Tasks — subscription-manager-cleanup

## API redesign (M2 + M3 + M4)
- [x] `ISubscriptionManager` → generic `ISubscriptionManager<TEventType, TEventArgs>`; `object` params →
      `IReadOnlyCollection<TEventType>` + `TEventArgs`/`TEventType`; `account` → `topic`.
- [x] `AbstractSubscriptionManager<TEventType, TEventArgs>`: public `Subscribe`/`Unsubscribe`/
      `UpdateSubscription` become template wrappers over abstract `*Core` methods, run under
      `OperationLock` via `WithLockAsync` (M2); `GetClientsForEvent` stays abstract + lock-free;
      `WithLockAsync` throws `ObjectDisposedException` after dispose. Dispose-drain (rule #3) preserved.
- [x] Generic `AddJsonRpcCore<…, TEventType, TEventArgs>` registers `ISubscriptionManager<…>` to the
      concrete singleton.

## Consumers
- [x] `example/SimpleServer`: `DemoSubscriptionManager` (generic, `*Core` overrides, no re-entrant lock),
      `DemoEventsRpcAdapter` (injects generic interface, `topic`), `IDemoEventsRpc` (`topic`), `Program.cs`
      (7 type args).
- [x] Tests: `TestSubscriptionManager` generic + `*Core`; composition test updated for the generic interface.

## Guards + close-out
- [x] `Subscribe_WhileOperationLockHeld_DoesNotProceedUntilReleased` (M2: base serializes via the lock).
- [x] `Subscribe_AfterDispose_ThrowsObjectDisposedException` (typed state error).
- [x] `dotnet build` 0 warnings (audit on) + `dotnet test` green (112 → 114).
- [x] Version bump `1.3.0 → 2.0.0` (breaking).
- [x] Doc-sync: `AUDIT-FINDINGS.md`, `CLAUDE.md` (new critical rule + backlog + test count + version),
      `.claude/rules/{audit-debt,patterns}.md`.
