# Design — secure-transport-mtls

How the three capabilities fit the existing abstract-base-class transport, and the non-obvious
integration constraints (verified against NetCoreServer 8.0.7 + .NET 10 BCL via Microsoft Learn).

## Constraint that shapes everything: NetCoreServer's validation seam

NetCoreServer 8.0.7 exposes SSL via `WssServer`/`WssSession` + `SslContext`. Confirmed from the shipped
`NetCoreServer.dll`: `SslContext` has `ClientCertificateRequired` and `RemoteCertificateValidationCallback`,
and authentication runs through `BeginAuthenticateAsServer`/`EndAuthenticateAsServer` (the APM
`SslStream` path) — **not** the modern `SslServerAuthenticationOptions` with `CertificateChainPolicy`.

Consequence: we **cannot** hand the runtime an `X509ChainPolicy` (`CustomRootTrust` / `CustomTrustStore`
/ `RevocationMode`) declaratively. All custom validation must happen **inside**
`RemoteCertificateValidationCallback`, where we build the `X509Chain` ourselves. The default
`CustomRootTrustValidator` therefore:

```
bool Validate(X509Certificate2 clientCert, X509Chain _, SslPolicyErrors errors):
    using chain = new X509Chain
    chain.ChainPolicy.TrustMode      = X509ChainTrustMode.CustomRootTrust   // private CA, not machine store
    chain.ChainPolicy.CustomTrustStore.AddRange(_options.TrustedRoots)
    chain.ChainPolicy.RevocationMode = _options.RevocationMode              // default Offline (cached CRL)
    chain.ChainPolicy.ApplicationPolicy.Add(ClientAuthOid "1.3.6.1.5.5.7.3.2")  // EKU clientAuth
    if not chain.Build(clientCert): log + return false
    if _options.SpkiPins is not empty and Spki(clientCert) not in _options.SpkiPins: log + return false
    return true
```

Notes grounded in Microsoft TLS/SSL best-practices:
- **Never `return true` blindly** — the whole point of the callback. The Roslyn guard pins this.
- **Revocation default `Offline`** (cached CRL): `Online` makes external OCSP/CRL/AIA calls during the
  handshake → DoS exposure if the responder is slow. Pair `Offline` with short-lived certs. (.NET 11
  disables server-side AIA downloads by default anyway.) `NoCheck` is allowed only with a justification
  comment (guarded).
- **SPKI pin** (SHA-256 of `SubjectPublicKeyInfo`) is defense-in-depth on top of CA trust, and survives
  certificate re-issuance with the same key (unlike a thumbprint pin).

## Capability 1: `tls-transport`

- New `AbstractSecureJsonRpcServer(SslContext context, IPAddress address, int port, IServiceProvider sp,
  ILogger logger) : WssServer(context, address, port)`. Mirrors `AbstractJsonRpcServer` exactly (same
  `CreateJsonRpcSession()` abstract seam; session creation unchanged) — the only delta is the SSL base +
  the `SslContext`.
- `TlsServerOptions` (validated record, options pipeline like `JsonRpcServerConfig`): server certificate
  source, `SslProtocols` (default `Tls13 | Tls12`), client-cert requirement toggle (on for mTLS).
- Server cert → `SslStreamCertificateContext.Create(...)` **once** at composition (chain build is
  CPU-intensive; reuse enables TLS session resumption on Linux). NetCoreServer's `SslContext` takes the
  `X509Certificate2`; we hold the context for reuse where the API allows.
- DI: a parallel generic composition root
  `AddSecureJsonRpcCore<…>(Action<JsonRpcServerConfig>?, Action<TlsServerOptions>?)` that builds the
  `SslContext` from validated `TlsServerOptions` and wires `AbstractSecureJsonRpcServer`. Plaintext
  `AddJsonRpcCore<…>` stays untouched.

## Capability 2: `mtls-node-identity`

- `SslContext.ClientCertificateRequired = true` + `SslContext.RemoteCertificateValidationCallback`
  delegated to the resolved `INodeCertificateValidator`.
- `NodeIdentity` (readonly record struct): `Uri? SpiffeId`, `string SpkiThumbprint`, `string Subject`.
- `INodeIdentityResolver` default extracts the SAN URI (SPIFFE-style) as the principal name, SPKI-SHA-256
  as the stable fallback id. Pluggable so a consumer wanting OU-based identity overrides one type.
- On `OnWsConnected` (in `AbstractJsonRpcSession`, secure path): the validated cert → `NodeIdentity` →
  `ClaimsPrincipal` (claims: `name` = SPIFFE id or SPKI, `spki`, role claims if a resolver supplies any).
  Stored on a new `protected ClaimsPrincipal? Principal { get; }` on the session, set before
  `RegisterServices`/`StartListening`.
- A failed validation aborts the handshake at the TLS layer — RPC dispatch is never reached, so there is
  no "authenticated == false but methods callable" window.

## Capability 3: `rpc-authorization`

- `[RpcAuthorize(Roles = "events:write,admin")]` — `AttributeUsage(Method | Interface)`.
- `IRpcAuthorizationPolicy.Authorize(ClaimsPrincipal principal, RpcAuthorizeAttribute requirement) →
  bool`. Default `StaticRoleMapAuthorizationPolicy` maps `NodeIdentity → roles` from consumer config and
  checks role membership.
- Enforcement at **both** dispatch paths (single source of truth for the check, two call sites):
  - **Reflection path** (`RegisterServicesViaReflection` / `AddLocalRpcTarget`): wrap via a
    `JsonRpc` interceptor / `JsonRpcMethodAttribute`-aware target wrapper that consults the policy before
    invoking; on deny throw `RpcErrorException(-32001)`.
  - **Generated binder path** (`IRpcMethodBinder`): the generator emits the policy check at the head of
    each `AddLocalRpcMethod` delegate for attributed methods (keeps the AOT-clean, no-reflection
    property — the attribute is read at compile time, the check is a plain method call at runtime).
- **Deny-by-default applies only to attributed methods** — un-attributed methods are unrestricted, so the
  feature is purely additive and existing consumers see no behavior change until they annotate.
- The principal comes from the session (capability 2); for client-aware services it is already
  constructed per-connection, so the policy sees the right identity.

## Logging + guards

- New source-gen `[LoggerMessage]` partials (rule #10): `AbstractSecureJsonRpcServerLog` +
  `NodeCertificateValidatorLog` (EventId **1500–1599**), `RpcAuthorizationLog` (**1600–1699**). Privacy:
  log SPKI/subject/SPIFFE-id + decision enum only — never private keys or full cert PEM.
- Regression guards (`tests/WsRpcServer.Tests/Security/`):
  - `CustomRootTrustValidator` rejects self-signed / unknown-CA / expired / wrong-EKU certs.
  - SPKI-allowlist mismatch rejected; match accepted.
  - SAN URI → `NodeIdentity.SpiffeId`; missing SAN → SPKI fallback.
  - `[RpcAuthorize]` deny-by-default: principal without role → `RpcErrorException(-32001)`, method body
    not entered; with role → invoked.
  - Roslyn structural guard (analog of `ConfigureAwaitConventionTests`): no
    `RemoteCertificateValidationCallback => true` and no `X509RevocationMode.NoCheck` in `src/WsRpcServer`
    without an adjacent `// justification:` comment.

## Why not alternatives

- **JWT / RFC 8705 certificate-bound tokens** — rejected: node-identity only, no user-identity; adds IdP
  + token-lifetime-vs-long-lived-WS refresh + bearer-theft surface for zero added expressiveness.
- **mTLS in SignalCliNet.WsRpcServer** — rejected: transport concern; consumer-side would fork transport
  (its CLAUDE.md rule #1) and not benefit other consumers.
- **`SslServerAuthenticationOptions.CertificateChainPolicy`** — unavailable through NetCoreServer's APM
  auth path; hence manual `X509Chain` in the callback.
