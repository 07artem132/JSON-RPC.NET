# Tasks — security-hardening

## 1. dependency-vuln-messagepack
- [x] 1.1 Pin `MessagePack` 2.5.302 (patched) in `src/WsRpcServer/WsRpcServer.csproj`.
- [x] 1.2 Verify `dotnet build` passes with audit ON (no `-p:NuGetAudit=false`); `dotnet list package --vulnerable` clean.

## 2. parse-failure-throttle (H2 + M9)
- [x] 2.1 Add `MaxConsecutiveParseFailures` (default 10) to `JsonRpcServerConfig`.
- [x] 2.2 `WebSocketMessageHandler`: count consecutive `JsonException`s; close `ProtocolError` + end read loop on limit; reset on success.
- [x] 2.3 `CanRead`/`CanWrite` → `!_disposed`.
- [x] 2.4 Guard test: N malformed frames → session closed with `ProtocolError`; one good frame resets the counter. (`WebSocketMessageHandlerParseThrottleTests`; existing recovery test updated Error→Warning.)

## 3. dispose-cancellation (H4)
- [ ] 3.1 `AbstractJsonRpcSession.Dispose`: `Cts.Cancel()` → complete channel → bounded drain of `NotificationProcessingTask` → `Cts.Dispose()`; idempotent.
- [ ] 3.2 `AbstractEventProcessor.Dispose(bool)`: `Cts.Cancel()` before `Cts.Dispose()`.
- [ ] 3.3 `AbstractSubscriptionManager.Dispose(bool)`: best-effort drain `OperationLock` before dispose.
- [ ] 3.4 Guard test: dispose during in-flight notification cancels + drains without `ObjectDisposedException`.

## 4. service-registry-thread-safety (H3)
- [ ] 4.1 Replace `??=` lazy cache with thread-safe init (double-checked `Lock` + volatile).
- [ ] 4.2 Log Warning when >1 implementation found for one interface.
- [ ] 4.3 Guard test: `Parallel.For` first-use builds the cache exactly once.

## 5. Close-out
- [ ] 5.1 Bump `<WsRpcServerPackageVersion>` 1.1.0 → 1.2.0.
- [ ] 5.2 `AUDIT-FINDINGS.md` + `CLAUDE.md`: move H2/H3/H4 to shipped + cite this change.
- [ ] 5.3 Full `dotnet build` (audit on) + `dotnet test` green.
