# observability-and-resilience — метрики/трейси + квота з'єднань → 2.7.0

## Why

Бек-лог `AUDIT-FINDINGS.md` порожній, authn/authz зашиплено (2.6.0). У README-roadmap відкритим лишився
пункт **«Аналітика та моніторинг»**, а захист від DoS наразі покриває лише розмір повідомлення
(`MaxMessageSizeBytes`) і потік невалідного JSON (`MaxConsecutiveParseFailures`) — немає **ні видимості**
(скільки з'єднань, скільки відмов авторизації, чи відкидаються сповіщення), **ні квоти** на кількість
одночасних з'єднань. Обидва — операційні інваріанти, які мають жити у фреймворку (він володіє транспортом),
а не перевинаходитись кожним споживачем.

Дзеркалимо обсервабіліті-патерн із sibling-репо **SignalCli.NET** (`post-modernize-tuning §11`):
`ActivitySource` + `Meter` з **приватністю як інваріантом** — теги лише з відомого безпечного набору
(імена методів / статуси / лічильники), ніколи payload / identity-секрети.

Без нової NuGet-залежності: `System.Diagnostics.DiagnosticSource` (метрики/трейси) уже транзитивно
присутній; квота — на наявних seam'ах `TcpServer.OnConnected/OnDisconnected`.

## What Changes

| # | Capability | Surface | Files | Risk |
|---|---|---|---|---|
| 1 | `observability` | `WsRpcServerDiagnostics` (`Meter` "WsRpcServer" + `ActivitySource`) + інструментація 4 точок | new `Diagnostics/WsRpcServerDiagnostics.cs`, hooks у `Core/AbstractJsonRpcServer`, `Sessions/Abstract*Session`, `Transport/WebSocketMessageHandler`, `Authorization/RpcAuthorizationEnforcer` | low |
| 2 | `connection-resilience` | `JsonRpcServerConfig.MaxConcurrentConnections` + enforcement у сервері | `Core/JsonRpcServerConfig.cs`, `Core/AbstractJsonRpcServer.cs`, `Logging/AbstractJsonRpcServerLog.cs` | low |

**Decision (метрики, не лише трейси):** основна цінність для service-to-service — **лічильники/гейджі**
(`Meter`), бо їх чисто розмістити на framework-owned seam'ах; `ActivitySource` додаємо як span життєвого
циклу з'єднання (старт у `OnConnected`, стоп у `OnDisconnected`) — корелюється per-connection.

**Decision (квота на TCP-рівні):** `MaxConcurrentConnections` enforce'иться у `AbstractJsonRpcServer.OnConnected`
(framework-owned, не конфліктує зі споживацьким `OnWsConnected`); відмова = `session.Disconnect()` + лог +
метрика `connections.rejected`. `0` = без ліміту (back-compat дефолт).

**Out of scope (свідомо, окремими змінами):** idle-timeout (seam `OnWsReceived` належить споживачу — потрібен
явний opt-in API, не прозоро); graceful draining на shutdown (NetCoreServer `Stop()` синхронний і рве всі
з'єднання — «stop accepting + drain in-flight» це окрема більша робота); per-`NodeIdentity` ліміти (надбудова
над цією квотою + authz-шаром). Окремий пакет `JSON-RPC.NET.HealthChecks` (version-lockstep) — наступна
ітерація, після того як core-метрики стабілізуються.

## Capabilities

### `observability`
Бібліотека SHALL експонувати `Meter` з ім'ям `"WsRpcServer"` з інструментами: `wsrpc.connections.active`
(UpDownCounter), `wsrpc.connections.rejected` (Counter), `wsrpc.notifications` (Counter, тег `result` ∈
{`queued`,`dropped`}), `wsrpc.parse_failures` (Counter), `wsrpc.authorization.denied` (Counter) — та
`ActivitySource` з ім'ям `"WsRpcServer"` зі span'ом життєвого циклу з'єднання. Теги SHALL походити лише з
фіксованого безпечного набору (`result`); жодних message-body / phone / identity-секретів. Інструментація
не повинна впливати на поведінку, якщо немає підписників (метрики/трейси — pull/opt-in).

### `connection-resilience`
`JsonRpcServerConfig` SHALL нести `MaxConcurrentConnections` (`[Range(0, int.MaxValue)]`, дефолт `0` =
без ліміту). Коли `> 0` і кількість активних з'єднань перевищує ліміт, сервер SHALL відхилити нове
з'єднання (`Disconnect`), залогувати Warning і збільшити `wsrpc.connections.rejected` — до RPC-диспетчу
відхилене з'єднання не доходить. `0` зберігає поточну поведінку (additive).

## Verification

- `dotnet build JSON-RPC.NET.sln` — 0 warnings (lib + tests `TreatWarningsAsErrors`), audit on.
- `dotnet test` — наявна сюїта + нові guard'и зелені (158 → ~166). Guards: `MeterListener` ловить
  `connections.active`/`notifications{result}`/`rejected`; privacy-guard пінить, що набір тег-ключів
  ⊆ {`result`}; квота відхиляє (N+1)-ше з'єднання, дефолт `0` не змінює поведінки.
- EventId: відмова з'єднання → блок сервера (1000–1099, наразі вільний).
- Версія `2.6.0 → 2.7.0` (additive); `CLAUDE.md` + `README.md` (roadmap «Аналітика» → shipped) +
  `docs/api/observability.md` (новий) + `composition-and-config.md` (нове поле конфігу) оновлено;
  `DocsApiCoverageTests` покриває новий публічний тип.
