# Spec — tls-transport

## ADDED Requirements

### Requirement: A secure server SHALL serve wss:// from a configured certificate without affecting the plaintext server

A new `AbstractSecureJsonRpcServer : NetCoreServer.WssServer` SHALL accept TLS WebSocket connections,
exposing the same `protected abstract WsSession CreateJsonRpcSession()` extension seam as
`AbstractJsonRpcServer`. The existing plaintext `AbstractJsonRpcServer` SHALL remain unchanged — TLS is
an opt-in additive capability, not a replacement.

#### Scenario: Secure server accepts a TLS client

- **GIVEN** `AddSecureJsonRpcCore<…>` configured with a valid server certificate
- **WHEN** the server is started and a `wss://` client completes the TLS handshake
- **THEN** the connection is established and RPC dispatch proceeds as on the plaintext server

#### Scenario: Plaintext path is untouched

- **GIVEN** an existing consumer using `AddJsonRpcCore<…>` (no TLS)
- **WHEN** it is built against this version
- **THEN** it compiles and serves `ws://` with no behavior change

### Requirement: TLS configuration SHALL be validated fail-fast and reuse the certificate context

`TlsServerOptions` SHALL be validated through the options pipeline (server certificate required;
`SslProtocols` default `Tls13 | Tls12`). The server certificate SHALL be built once into a reusable
`SslStreamCertificateContext` (chain build is CPU-intensive). Invalid TLS configuration SHALL surface as
`OptionsValidationException` on resolve, not deep inside the TLS handshake.

#### Scenario: Missing server certificate fails fast

- **GIVEN** `AddSecureJsonRpcCore<…>` with no server certificate configured
- **WHEN** the options are resolved
- **THEN** an `OptionsValidationException` is thrown

#### Scenario: Minimum protocol enforced

- **GIVEN** `TlsServerOptions` with default protocols
- **WHEN** the `SslContext` is built
- **THEN** it negotiates TLS 1.3 (falling back to 1.2) and refuses older protocols
