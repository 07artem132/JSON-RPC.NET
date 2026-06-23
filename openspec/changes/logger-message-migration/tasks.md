# Tasks — logger-message-migration

## logger-message-migration

- [x] Create `src/WsRpcServer/Logging/AbstractJsonRpcServerLog.cs` (block 1000–1099).
- [x] Create `src/WsRpcServer/Logging/AbstractJsonRpcSessionLog.cs` (block 1100–1199).
- [x] Create `src/WsRpcServer/Logging/WebSocketMessageHandlerLog.cs` (block 1200–1299).
- [x] Create `src/WsRpcServer/Logging/AbstractRpcServiceRegistryLog.cs` (block 1300–1399).
- [x] Create `src/WsRpcServer/Logging/AbstractEventProcessorLog.cs` (block 1400–1499).
- [x] Replace all `Logger.LogX(...)` / `_logger.LogX(...)` call sites in the 5 production files with the
      generated partials, preserving message text + level.
- [x] Remove `CA1848;CA1873` from the `<NoWarn>` in `Directory.Build.props`; drop the now-stale comment.
- [x] Add regression guard `tests/WsRpcServer.Tests/Logging/LoggerMessageMigrationTests.cs` — assert no
      `ILogger.Log*("template")` call remains in `src/WsRpcServer/**` and EventId blocks don't collide.
- [x] `dotnet build` 0 warnings (CA1848/CA1873 now active) + `dotnet test` green (114 → 116).
- [x] Scope CA1848/CA1873 suppression to the test csproj (ad-hoc test-double logging) and
      `example/Directory.Build.props` (idiomatic consumer demos); update test-mock `IsEnabled` setup.
- [x] Doc-sync: `CLAUDE.md` (critical rule #2 + new rule #10, version → 2.1.0, Implemented/planned,
      backlog, test count), `csproj-build.md`, `patterns.md` (new Logging section), `AUDIT-FINDINGS.md`.
- [x] Version bump `Directory.Build.props` `<WsRpcServerPackageVersion>` 2.0.0 → 2.1.0 (trailing commit).
