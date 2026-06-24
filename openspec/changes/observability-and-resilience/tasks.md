# Tasks — observability-and-resilience

## observability
- [x] Add `Diagnostics/WsRpcServerDiagnostics.cs`: static `Meter` + `ActivitySource` ("WsRpcServer"),
      instruments (`connections.active`/`rejected`, `notifications{result}`, `parse_failures`,
      `authorization.denied`), `AllowedTagKeys` allowlist + record helpers.
- [x] Wire: notification queued/dropped in `AbstractJsonRpcSession.SendNotificationAsync` +
      `AbstractSecureJsonRpcSession.SendNotificationAsync`.
- [x] Wire: parse failure in `WebSocketMessageHandler` recovery loop.
- [x] Wire: auth denial in `RpcAuthorizationEnforcer.Enforce`.
- [x] Wire: connection span + active gauge in `AbstractJsonRpcServer.OnConnected`/`OnDisconnected`.
- [x] Regression guards: `MeterListener` sees `connections.active` +1/-1, `notifications{result}`;
      privacy-guard: captured tag keys ⊆ `{ "result" }`.

## connection-resilience
- [x] Add `JsonRpcServerConfig.MaxConcurrentConnections` (`[Range(0, int.MaxValue)]`, default 0).
- [x] Enforce in `AbstractJsonRpcServer.OnConnected`: over-limit → Warning + `connections.rejected` +
      `Disconnect`; CWT marker so only accepted connections inc/dec the gauge + own a span.
- [x] `[LoggerMessage]` connection-rejected in `AbstractJsonRpcServerLog` (server block 1000–1099).
- [x] Regression guards: `(N+1)`-th connection rejected; default `0` rejects nothing.

## Close-out
- [x] `dotnet build` 0 warnings (lib + tests) + `dotnet test` green (158 → ~166).
- [x] `docs/api/observability.md` (new) + `docs/README.md` index; `MaxConcurrentConnections` row in
      `docs/api/composition-and-config.md`; `DocsApiCoverageTests` green on new public type.
- [x] Version bump `2.6.0 → 2.7.0` in `Directory.Build.props` (additive).
- [x] Doc-sync: `CLAUDE.md` (Implemented entry + test count + EventId note), `README.md` (roadmap
      «Аналітика та моніторинг» → shipped), `.claude/rules/patterns.md` (observability + quota section).
