# Tasks — secure-transport-mtls

## tls-transport
- [ ] Add `Security/TlsServerOptions.cs` (validated record: `ServerCertificate` source, `SslProtocols`
      default `Tls13 | Tls12`, `ClientCertificateRequired` toggle) + source-gen `[OptionsValidator]`.
- [ ] Add `Core/AbstractSecureJsonRpcServer.cs : WssServer` mirroring `AbstractJsonRpcServer`'s
      `CreateJsonRpcSession()` seam; build `SslStreamCertificateContext` once and hold for reuse.
- [ ] Add `AddSecureJsonRpcCore<…>(Action<JsonRpcServerConfig>?, Action<TlsServerOptions>?)` composition
      root that constructs the `SslContext` from validated options; leave plaintext `AddJsonRpcCore<…>`
      untouched.
- [ ] Source-gen `[LoggerMessage]` `AbstractSecureJsonRpcServerLog` (EventId 1500–1549).
- [ ] Regression guard: secure server starts with a valid cert; invalid/empty `TlsServerOptions` →
      `OptionsValidationException` (fail-fast).

## mtls-node-identity
- [ ] Add `Security/INodeCertificateValidator.cs` + default `Security/CustomRootTrustValidator.cs`
      (manual `X509Chain`: `CustomRootTrust` + `CustomTrustStore`, `RevocationMode` default `Offline`,
      EKU `clientAuth`, optional SPKI-SHA-256 pin allowlist; reject on chain error).
- [ ] Add `Security/NodeIdentity.cs` (readonly record struct) + `Security/INodeIdentityResolver.cs`
      with a default SAN-URI (SPIFFE) resolver, SPKI fallback.
- [ ] Wire `SslContext.ClientCertificateRequired = true` + `RemoteCertificateValidationCallback` →
      validator; on success resolve `NodeIdentity` → `ClaimsPrincipal`.
- [ ] Add `protected ClaimsPrincipal? Principal { get; }` to `AbstractJsonRpcSession`; set on the secure
      `OnWsConnected` before `RegisterServices`/`StartListening`.
- [ ] Source-gen `[LoggerMessage]` `NodeCertificateValidatorLog` (EventId 1550–1599); log
      SPKI/subject/SPIFFE-id + decision enum only (privacy).
- [ ] Regression guards: self-signed / unknown-CA / expired / wrong-EKU rejected; SPKI mismatch rejected,
      match accepted; SAN→`SpiffeId`, missing SAN→SPKI fallback.

## rpc-authorization
- [ ] Add `Authorization/RpcAuthorizeAttribute.cs` (`Roles`, `AttributeUsage(Method | Interface)`).
- [ ] Add `Authorization/IRpcAuthorizationPolicy.cs` + default `StaticRoleMapAuthorizationPolicy`
      (`NodeIdentity → roles` from consumer config).
- [ ] Enforce in reflection path (`AbstractRpcServiceRegistry`) and generated binder path
      (`IRpcMethodBinder` — generator emits the check head-of-delegate, stays AOT-clean): deny-by-default
      for attributed methods → `RpcErrorException(-32001)` before method body; un-attributed unrestricted.
- [ ] Source-gen `[LoggerMessage]` `RpcAuthorizationLog` (EventId 1600–1699).
- [ ] Regression guards: principal without role → `RpcErrorException(-32001)`, body not entered; with
      role → invoked; un-attributed method callable by any principal.
- [ ] Generator guard: a `[RpcAuthorize]` method emits the policy check in the generated binder.

## Cross-cutting
- [ ] Roslyn structural guard (`tests/WsRpcServer.Tests/Security/CertificateValidationConventionTests`):
      no `RemoteCertificateValidationCallback => true` and no `X509RevocationMode.NoCheck` in
      `src/WsRpcServer` without an adjacent `// justification:` comment. Verify non-vacuous.
- [ ] No new NuGet dependency (NetCoreServer `WssServer` + BCL X509/Claims only).

## Close-out
- [ ] `dotnet build` 0 warnings (lib + tests, audit on) + `dotnet test` green (134 → ~146).
- [ ] `docs/api/security.md` (new) + `docs/README.md` index entry; `DocsApiCoverageTests` green on new
      public types.
- [ ] Version bump `2.5.0 → 2.6.0` in `Directory.Build.props` (additive feature).
- [ ] Doc-sync: `CLAUDE.md` (new rule + Implemented entry + test count), `README.md` (Roadmap → shipped
      for authn/authz), `.claude/rules/patterns.md` (secure transport + authz section).
- [ ] Downstream (separate change): `SignalCliNet.WsRpcServer` bumps `JSON-RPC.NET` → 2.6.0, wires cert +
      private CA + SPKI allowlist + node→roles via `AddSignalJsonRpc` (rule #2).
