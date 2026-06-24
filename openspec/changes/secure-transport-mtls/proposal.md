# secure-transport-mtls — TLS transport + mTLS node identity + RPC authorization → 2.6.0

## Why

The library currently serves only plaintext `ws://` (`AbstractJsonRpcServer : WsServer`) with **no
authentication and no authorization** — any client that can open a socket can invoke every registered
RPC method. The README roadmap names "Авторизація та аутентифікація" as the next feature track, and the
sibling consumer **SignalCliNet.WsRpcServer** exposes a sensitive surface (Signal account/device/message
operations) that should not be reachable by an unauthenticated peer.

The chosen model is **mutual-TLS with node identity** (machine-to-machine), decided over JWT/OAuth:

- The deployment is service-to-service — the identity that matters is the **node** (which service
  connected), not an end user. There is no user-identity distinct from node-identity, so a token layer
  (JWT, even certificate-bound per RFC 8705) adds an external IdP, a token-lifetime-vs-long-lived-WS
  refresh problem, and a bearer-theft surface for **no** additional expressiveness. mTLS alone is
  simpler and sufficient.
- mTLS authenticates the connection **once** at the TLS handshake and stays stable for the connection's
  lifetime (= certificate validity) — a natural fit for a long-lived WebSocket.

**Placement (mechanism vs policy):** this is a **transport-layer** concern, so the *mechanism* belongs
in this framework (it already owns the WebSocket transport — `AbstractJsonRpcServer` wraps NetCoreServer
`WsServer`). Per `SignalCliNet.WsRpcServer` CLAUDE.md rule #1 ("glue is not the place to implement
transport"), the consumer only supplies *policy/config* (which CA, which SPKI pins, the node→roles map,
the actual certificates) via its existing `AddSignalJsonRpc` composition root. Putting mTLS in the
consumer would fork transport behavior and force every future consumer to reinvent it.

Each capability ships with a regression-guard test (per `.claude/rules/audit-debt.md` — "a fix without a
guard is the next regression"). No new NuGet dependency: TLS uses NetCoreServer's existing `WssServer`,
validation uses BCL `System.Security.Cryptography.X509Certificates`, authorization uses
`System.Security.Claims`.

## What Changes

| # | Capability | New surface | Files | Risk |
|---|---|---|---|---|
| 1 | `tls-transport` | `AbstractSecureJsonRpcServer : WssServer` + TLS config | new `Core/AbstractSecureJsonRpcServer.cs`, `JsonRpcServerConfig.cs` (TLS fields), new `Security/TlsServerOptions.cs`, `Logging/` | medium |
| 2 | `mtls-node-identity` | `ClientCertificateRequired` + `INodeCertificateValidator` + `NodeIdentity`→`ClaimsPrincipal` on session | new `Security/INodeCertificateValidator.cs`, `Security/CustomRootTrustValidator.cs`, `Security/NodeIdentity.cs`, `Sessions/AbstractJsonRpcSession.cs` (principal), `Logging/` | medium |
| 3 | `rpc-authorization` | `[RpcAuthorize]` + deny-by-default enforcement at dispatch | new `Authorization/RpcAuthorizeAttribute.cs`, `Authorization/IRpcAuthorizationPolicy.cs`, `Services/AbstractRpcServiceRegistry.cs` + `IRpcMethodBinder` enforcement, `Logging/` | medium |

**Decision (roles source):** node identity is resolved from the client certificate's **SAN URI**
(SPIFFE-style `spiffe://…`, with SPKI SHA-256 as the stable fallback id); authorization roles come from a
**static `identity → roles` map** supplied by the consumer (default `IRpcAuthorizationPolicy`). Encoding
roles in the certificate (OU) is documented as an alternative but is **not** the spec'd default — it ties
role changes to certificate re-issuance. Identity extraction is pluggable (`INodeIdentityResolver`) so the
SAN-vs-OU choice is a consumer override, not a fork.

**Out of scope:** JWT / token layer (rejected above); end-user identity; client-side certificate
presentation (that is whoever connects, not this server framework); flipping `<IsAotCompatible>`
(unchanged — TLS/auth code is reflection-free but the StreamJsonRpc payload blocker from rule #4 stands);
SignalCliNet.WsRpcServer wiring (a separate downstream follow-up at the version bump — see Verification).

## Capabilities

### `tls-transport`
A new `AbstractSecureJsonRpcServer : NetCoreServer.WssServer` SHALL serve `wss://` from a configured
server certificate, leaving the existing plaintext `AbstractJsonRpcServer` untouched (TLS is opt-in,
non-breaking). TLS configuration (server certificate source, minimum protocol — default TLS 1.3 with 1.2
fallback) SHALL live in a validated `TlsServerOptions` wired through the options pipeline. The server
certificate SHALL be built once into a reusable `SslStreamCertificateContext` (chain build is
CPU-intensive — Microsoft TLS/SSL best-practices). No secret material is hardcoded; the consumer supplies
the certificate.

### `mtls-node-identity`
The secure server SHALL require a client certificate (`SslContext.ClientCertificateRequired = true`) and
validate it through a pluggable `INodeCertificateValidator`. The default `CustomRootTrustValidator` SHALL
build an `X509Chain` with `TrustMode = X509ChainTrustMode.CustomRootTrust` + a consumer-supplied
`CustomTrustStore` (private CA — never the machine store), reject on any chain error, and support an
optional SPKI-SHA-256 pin allowlist (defense-in-depth). Because NetCoreServer surfaces validation only via
`SslContext.RemoteCertificateValidationCallback` (it calls `BeginAuthenticateAsServer`, not
`SslServerAuthenticationOptions.CertificateChainPolicy`), chain validation SHALL be performed inside that
callback. A validated certificate SHALL be resolved to a `NodeIdentity` (SAN URI, SPKI fallback) by an
`INodeIdentityResolver` and exposed as a `ClaimsPrincipal` on `AbstractJsonRpcSession`. An unvalidated /
untrusted / unpinned certificate SHALL fail the handshake (connection refused), not reach RPC dispatch.

### `rpc-authorization`
RPC interface methods MAY carry `[RpcAuthorize(Roles = …)]`. At dispatch (both the reflection
`RegisterServices` path and the generated `IRpcMethodBinder`), an authorization check SHALL run against
the session's `ClaimsPrincipal` via an `IRpcAuthorizationPolicy` (default = static `identity → roles`
map). The policy SHALL be **deny-by-default for attributed methods**: a method marked `[RpcAuthorize]`
invoked by a principal lacking the required role SHALL fail with an `RpcErrorException` (application code,
e.g. `-32001`) before the method body runs. Methods without the attribute keep current behavior
(unrestricted) so the change is additive.

## Verification

- `dotnet build JSON-RPC.NET.sln` — 0 warnings (lib + tests `TreatWarningsAsErrors`), NuGet audit on.
- `dotnet test` — existing suite + new guard tests green (134 → ~146 target). Guards: unknown-CA /
  self-signed handshake rejected; cert outside SPKI allowlist rejected; SAN→NodeIdentity mapping;
  deny-by-default for `[RpcAuthorize]`; Roslyn-guard forbidding `RemoteCertificateValidationCallback =>
  true` and `X509RevocationMode.NoCheck` without a justification comment (the "ходьба по колу" guard
  class).
- New library logging via source-generated `[LoggerMessage]` partials (rule #10); EventId blocks reserved
  **1500–1599** (secure transport + mTLS) and **1600–1699** (authorization).
- Version bump `2.5.0 → 2.6.0` in `Directory.Build.props` (additive feature, non-breaking); `CLAUDE.md`
  + `README.md` + `docs/` updated (new `docs/api/security.md`; `DocsApiCoverageTests` covers the new
  public types automatically).
- **Downstream follow-up (separate change, not this one):** `SignalCliNet.WsRpcServer` bumps the
  `JSON-RPC.NET` dependency to 2.6.0 and wires server cert + private CA + SPKI allowlist + node→roles map
  through `AddSignalJsonRpc` (CLAUDE.md rule #2 — upstream versions move together).
