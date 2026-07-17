# browser-ws-interop — subprotocol echo seam + text-frame опція → 2.8.0

## Why

Єдиний downstream-консюмер (`SignalCliNet.WsRpcServer`) відкрив два browser-interop беклог-пункти, які
зараз він обходить власноруч у своєму шарі. Обидва — про сумісність із **WHATWG-браузерним** `WebSocket`:

1. **Subprotocol-negotiation (101-upgrade дірка).** Перевірено з апстрім-сорсу **NetCoreServer 8.0.7**:
   `WsSession.OnWsConnecting(HttpRequest, HttpResponse)` викликається **ПІСЛЯ** `response.SetBody()` (на
   відміну від master-гілки апстріму, де ДО — commit `2c1cdc47`, 2026-05-27). Тож консюмерський
   `response.SetHeader("Sec-WebSocket-Protocol", …)` в override дописує заголовок **після** порожнього
   рядка-роздільника → він «протікає» у WebSocket-потік як сміття-кадр (WHATWG-клієнт бачить «reserved
   bits set» і не досягає open). Консюмер зараз обходить це, вручну перебудовуючи всю 101-відповідь —
   транспортна «glue»-логіка, яка має жити у фреймворку (він володіє транспортом), а не перевинаходитись.

2. **Text-frame для JSON.** Транспорт (`WebSocketMessageHandler.WriteCoreAsync`) шле JSON-RPC через
   `SendBinaryDataAsync` (Binary-кадр). WHATWG-браузер для JSON очікує **Text**-кадр (`event.data` = string,
   а не `Blob`). Немає опції надіслати текстом.

Обидва — **additive / opt-in, дефолт = поточна поведінка** (єдиний консюмер не має мовчки змінити
wire-поведінку). Без нової NuGet-залежності: усе на наявних seam'ах NetCoreServer.

## What Changes

| # | Capability | Surface | Files | Risk |
|---|---|---|---|---|
| 1 | `browser-ws-interop` (subprotocol) | `protected virtual string? NegotiateSubprotocol(IReadOnlyList<string>)` + `override bool OnWsConnecting(HttpRequest, HttpResponse)` у плейн-текст і захищеній сесіях; internal `WsUpgradeInterop` (parse + rebuild 101 за RFC 6455) | `Sessions/AbstractJsonRpcSession.cs`, `Sessions/AbstractSecureJsonRpcSession.cs`, new `Sessions/WsUpgradeInterop.cs`, `Logging/AbstractJsonRpcSessionLog.cs` | low |
| 2 | `browser-ws-interop` (text-frame) | `JsonRpcServerConfig.UseTextFramesForOutgoingMessages` (bool, def `false`) + `IJsonRpcSession.SendTextDataAsync` + `AbstractJsonRpcSession`/`AbstractSecureJsonRpcSession.SendTextDataAsync` + branch у `WriteCoreAsync` | `Core/JsonRpcServerConfig.cs`, `Sessions/IJsonRpcSession.cs`, `Sessions/Abstract*Session.cs`, `Transport/WebSocketMessageHandler.cs`, `Logging/AbstractJsonRpcSessionLog.cs` | low |

**Decision (перебудова 101 у фреймворку).** Замість того, щоб просити консюмера правильно збудувати
відповідь, база сама `Clear()` → `SetBegin(101)` → `Connection`/`Upgrade`/`Sec-WebSocket-Accept`
(RFC 6455 `Base64(SHA1(key + GUID))`) + `Sec-WebSocket-Protocol` серед заголовків → `SetBody()`. Це працює
незалежно від того, до чи після `SetBody` апстрім кличе `OnWsConnecting` (8.0.7 кличе після і не кличе
`SetBody` вдруге — `SendUpgrade` шле кеш як є). Хук повертає `null` за замовчуванням → база повертає
`base.OnWsConnecting(...)` (поведінка незмінна для тих, хто не override-ить).

**Decision (симетрія secure).** Захищена ієрархія (`AbstractSecureJsonRpcSession : WssSession`) отримує
ідентичні зміни: NetCoreServer розводить `WsSession`/`WssSession` в окремі ієрархії без спільного
WS-базового типу (CLAUDE.md rule #11), тож обидві мають однакову ваду й обидві потребують хука. Спільну
чисту логіку (парсинг + rebuild) винесено у internal `WsUpgradeInterop`, щоб не дублювати.

**Decision (дефолт binary).** `UseTextFramesForOutgoingMessages` дефолт `false` — пін поточної Binary-
поведінки; вмикання дає browser-interop. Прапорець булевий, `[Range]` не потрібен.

**Out of scope:** інші browser-quirks (permessage-deflate, автопінг-інтервал для браузера) — окремо за
потреби; вибір субпротоколу серед offered — політика консюмера (хук отримує повний список).

## Capabilities

### `browser-ws-interop`

**Subprotocol echo.** Базові сесії SHALL надати `NegotiateSubprotocol(IReadOnlyList<string> offered)` хук
(дефолт `null`). Під час 101-upgrade база SHALL розпарсити `Sec-WebSocket-Protocol` (кілька заголовків АБО
comma-list), викликати хук; якщо хук повернув не-`null` — SHALL ПОВНІСТЮ перебудувати 101-відповідь із
`Sec-WebSocket-Protocol` серед заголовків (RFC 6455) так, щоб клієнт досяг open без сміття-кадру. Якщо хук
повернув `null` — поведінка SHALL лишитись як `base.OnWsConnecting(...)` (незмінна).

**Text-frame.** `JsonRpcServerConfig` SHALL нести `UseTextFramesForOutgoingMessages` (bool, дефолт `false`).
Коли `true`, транспорт SHALL надсилати вихідні JSON-RPC кадри Text-ом (`SendTextDataAsync`); коли `false` —
Binary-ом (`SendBinaryDataAsync`, поточна поведінка). `IJsonRpcSession` SHALL нести `SendTextDataAsync`
симетрично до `SendBinaryDataAsync`.

## Verification

- `dotnet build JSON-RPC.NET.sln` — 0 warnings (lib + tests `TreatWarningsAsErrors`), audit on.
- `dotnet test` — наявна сюїта + нові guard'и зелені; база 164 не падає.
  - Loopback (`ClientWebSocket` із субпротоколом) → сервер echo-ить рівно узгоджений субпротокол,
    з'єднання досягає open, перший кадр після upgrade — валідний JSON-RPC (без сміття-кадру).
  - Loopback: `UseTextFramesForOutgoingMessages=true` → клієнт отримує Text-кадр; `false` (дефолт) → Binary.
- EventId: субпротокол/текст-send → session-блок 1100–1199 (нові 1117–1120).
- Версія `2.7.0 → 2.8.0` (additive); `CLAUDE.md` «Implemented» + `.claude/rules/patterns.md` оновлено.
  Немає `CHANGELOG.md` у репо (конвенція — не створювати).
