# logger-message-migration — source-generated logging (CA1848/CA1873) → 2.1.0

## Why

`Directory.Build.props` carries a repo-wide `<NoWarn>$(NoWarn);CA1848;CA1873</NoWarn>` — the only two
deferred analyzer suppressions in the library (critical rule #2). They mask the "use `LoggerMessage`
delegates" performance rule across ~51 `ILogger.Log*` call sites in `src/WsRpcServer`. Every call
currently boxes its arguments and re-parses the message template on each invocation; the hot paths
(per-message read/write in `WebSocketMessageHandler`, per-notification fan-out in
`AbstractJsonRpcSession` / `AbstractEventProcessor`) pay that cost on every frame.

This change closes the last deferred suppression by migrating all library logging to source-generated
`[LoggerMessage]` partial methods — the exact convention the sibling **SignalCli.NET** uses (one
`internal static partial class *Log` per type, EventId block reserved per type). With the migration done,
the blanket `NoWarn` is removed so any *new* ad-hoc `Logger.LogX("template", …)` call fails the build.

## What Changes

| # | Capability | Files | Risk |
|---|---|---|---|
| 1 | `logger-message-migration` | new `src/WsRpcServer/Logging/*Log.cs` ×5; 5 call-site files; `Directory.Build.props` | low — log output text unchanged |

The 5 call-site files and their reserved EventId blocks:

| Type | `*Log` class | EventId block |
|---|---|---|
| `Core/AbstractJsonRpcServer` | `AbstractJsonRpcServerLog` | 1000–1099 |
| `Sessions/AbstractJsonRpcSession` | `AbstractJsonRpcSessionLog` | 1100–1199 |
| `Transport/WebSocketMessageHandler` | `WebSocketMessageHandlerLog` | 1200–1299 |
| `Services/AbstractRpcServiceRegistry` | `AbstractRpcServiceRegistryLog` | 1300–1399 |
| `Events/AbstractEventProcessor` | `AbstractEventProcessorLog` | 1400–1499 |

**Out of scope:** registry AOT source-gen discovery alternative (still open), the LOW-severity polish
(L1–L7). Message text and log levels are preserved verbatim — this is a perf/hygiene refactor, not a
behavioral change.

## Capabilities

### `logger-message-migration`
Every `ILogger.Log{Trace,Debug,Information,Warning,Error}` call in `src/WsRpcServer/**` SHALL be replaced
by a source-generated `[LoggerMessage]` partial method declared in an `internal static partial class
<Type>Log` under `WsRpcServer.Logging`. Each method SHALL carry an explicit `EventId` from its type's
reserved block, preserve the original `LogLevel`, and preserve the original message template (structured
property names unchanged). Exception-carrying calls SHALL take the `Exception` as the parameter
immediately after `ILogger logger`. The repo-wide `CA1848;CA1873` `<NoWarn>` SHALL be removed from
`Directory.Build.props` so the library builds clean with both rules active and any future ad-hoc logging
call fails `TreatWarningsAsErrors`.

A regression guard SHALL assert that no production source file under `src/WsRpcServer` contains a direct
`ILogger.Log*("…")` template call (all logging goes through the generated partials).
