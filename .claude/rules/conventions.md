---
paths:
  - "src/**"
  - "tests/**"
  - "example/**"
---

# Conventions (match the existing code)

- Modern C#: file-scoped namespaces, records for DTOs/config, `var` only when the type is obvious,
  collection expressions / `required` where natural. `Func<>`/`Action<>` over custom delegates.
- `string`/`int` keywords, not `String`/`Int32`.
- `_camelCase` private fields, PascalCase public, `I`-prefixed interfaces. Abstract base classes are
  prefixed `Abstract*` (`AbstractJsonRpcServer`, `AbstractEventProcessor`, …) — this is the framework's
  primary extension seam; keep the prefix when adding new base types.
- Always `.ConfigureAwait(false)` in library code (`src/WsRpcServer/**`) — it ships as a NuGet package
  and must not capture a consumer's synchronization context.
- **Exceptions:** throw and catch *specific* types. A broad `catch (Exception)` is allowed **only** at
  long-running boundaries (the message-read loop, the notification fan-out) where one bad item must not
  kill the loop — and such catches must log and continue. Do not swallow exceptions silently elsewhere.
- Keep XML doc comments on public members (`<GenerateDocumentationFile>true</GenerateDocumentationFile>`
  is on; `CS1591` is the only doc-warning suppressed).
- **Comments and log messages are written in Ukrainian** in this codebase — match that when editing.
- **Test-class naming:** `*Tests` suffix, one class per file, folder mirrors the production namespace
  (`tests/WsRpcServer.Tests/Transport/WebSocketMessageHandlerTests.cs` tests
  `src/WsRpcServer/Transport/WebSocketMessageHandler.cs`). xUnit method naming
  `Method_Scenario_ExpectedResult` (underscores are why `CA1707` is suppressed in the test csproj only).
