# Tasks — browser-ws-interop

## subprotocol-negotiation seam
- [x] Add internal `Sessions/WsUpgradeInterop.cs`: `ParseOfferedSubprotocols(HttpRequest)` (кілька
      заголовків АБО comma-list) + `TryWriteUpgradeResponse(HttpRequest, HttpResponse, string)` (Clear →
      SetBegin(101) → Connection/Upgrade/Sec-WebSocket-Accept `Base64(SHA1(key+GUID))` + Sec-WebSocket-Protocol
      → SetBody).
- [x] `AbstractJsonRpcSession`: `protected virtual string? NegotiateSubprotocol(IReadOnlyList<string>)` (def
      `null`) + `override bool OnWsConnecting(HttpRequest, HttpResponse)` (хук→rebuild, інакше `base`).
- [x] `AbstractSecureJsonRpcSession`: симетричні `NegotiateSubprotocol` + `OnWsConnecting` (rule #11).
- [x] `[LoggerMessage]` `SubprotocolNegotiated` у `AbstractJsonRpcSessionLog` (session-блок, 1117).
- [x] Guard: реальний loopback `ClientWebSocket` із субпротоколом → сервер echo-ить рівно узгоджений
      субпротокол, з'єднання open, перший post-upgrade кадр — валідний (без сміття-кадру).

## text-frame option
- [x] `JsonRpcServerConfig.UseTextFramesForOutgoingMessages` (bool, def `false`; без `[Range]`).
- [x] `IJsonRpcSession.SendTextDataAsync(ReadOnlyMemory<byte>)` + реалізації в `AbstractJsonRpcSession`
      та `AbstractSecureJsonRpcSession` (через NetCoreServer `SendTextAsync`).
- [x] `WebSocketMessageHandler.WriteCoreAsync` читає прапорець → `SendTextDataAsync` / `SendBinaryDataAsync`.
- [x] `[LoggerMessage]` `TextSendAfterDispose`/`SendingTextData`/`TextSendError` (1118–1120).
- [x] Guard: loopback `true` → клієнт бачить Text-кадр; `false` (дефолт) → Binary (пін поточної поведінки).

## Close-out
- [x] `dotnet build` 0 warnings (lib + tests) + `dotnet test` green (база 164 не впала).
- [x] Version bump `2.7.0 → 2.8.0` у `Directory.Build.props` (additive).
- [x] Doc-sync: `CLAUDE.md` (Implemented entry + EventId note) + `.claude/rules/patterns.md` (browser-interop
      section). Немає `CHANGELOG.md` — не створюємо (конвенція репо).
