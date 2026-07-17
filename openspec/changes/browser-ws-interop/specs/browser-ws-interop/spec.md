# Spec — browser-ws-interop

## ADDED Requirements

### Requirement: The framework SHALL provide a subprotocol-negotiation seam for the 101 upgrade

The base sessions (`AbstractJsonRpcSession` and the secure `AbstractSecureJsonRpcSession`) SHALL expose a
`NegotiateSubprotocol(IReadOnlyList<string> offeredSubprotocols)` hook that returns the subprotocol to echo
back to the client, or `null`. The default implementation SHALL return `null`. During the WebSocket 101
upgrade the base SHALL parse the client's `Sec-WebSocket-Protocol` request header (multiple headers OR a
comma-separated list) and pass the offered subprotocols to the hook. When the hook returns a non-null value,
the base SHALL fully rebuild the 101 response with `Sec-WebSocket-Protocol` placed among the headers (before
the body) per RFC 6455, so the connection reaches OPEN with no stray frame leaking into the WebSocket stream.
When the hook returns `null`, behavior SHALL be identical to `base.OnWsConnecting(...)` (unchanged). This
applies symmetrically to the plaintext (`WsSession`) and secure (`WssSession`) hierarchies.

#### Scenario: Offered subprotocol is echoed and the connection opens cleanly

- **GIVEN** a server whose session overrides `NegotiateSubprotocol` to return an offered subprotocol
- **WHEN** a `ClientWebSocket` connects offering that subprotocol
- **THEN** the handshake succeeds, the negotiated subprotocol is echoed in the 101 response, the connection
  reaches OPEN, and the first frame after the upgrade is a valid JSON-RPC frame (no garbage frame)

#### Scenario: Default hook leaves behavior unchanged

- **GIVEN** a session that does not override `NegotiateSubprotocol` (default returns `null`)
- **WHEN** a client connects (with or without offering a subprotocol)
- **THEN** the 101 response is exactly what `base.OnWsConnecting(...)` produces (no `Sec-WebSocket-Protocol`)

### Requirement: The framework SHALL support text frames for outgoing JSON-RPC messages (opt-in)

`JsonRpcServerConfig` SHALL carry `UseTextFramesForOutgoingMessages` (bool, default `false`). When `true`,
the transport SHALL send outgoing JSON-RPC frames as WebSocket Text frames; when `false` (default), as Binary
frames (current behavior). `IJsonRpcSession` SHALL carry `SendTextDataAsync(ReadOnlyMemory<byte>)` symmetric
to `SendBinaryDataAsync`, implemented in both the plaintext and secure base sessions via NetCoreServer
`SendTextAsync`.

#### Scenario: Text frame when enabled

- **GIVEN** `UseTextFramesForOutgoingMessages = true`
- **WHEN** the server sends a JSON-RPC message to a loopback `ClientWebSocket`
- **THEN** the client receives a frame of type `WebSocketMessageType.Text`

#### Scenario: Binary frame by default

- **GIVEN** `UseTextFramesForOutgoingMessages = false` (default)
- **WHEN** the server sends a JSON-RPC message to a loopback `ClientWebSocket`
- **THEN** the client receives a frame of type `WebSocketMessageType.Binary` (behavior unchanged)
