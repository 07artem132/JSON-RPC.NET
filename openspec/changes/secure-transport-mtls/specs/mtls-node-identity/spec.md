# Spec — mtls-node-identity

## ADDED Requirements

### Requirement: The secure server SHALL require and validate a client certificate against a private trust root

The secure server SHALL set `SslContext.ClientCertificateRequired = true` and validate the presented
client certificate through a pluggable `INodeCertificateValidator`. The default
`CustomRootTrustValidator` SHALL build an `X509Chain` with
`ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust` and a consumer-supplied `CustomTrustStore`
(the private CA — never the machine store), require the `clientAuth` EKU, and reject the certificate on
any chain-build failure. Validation SHALL be performed inside `RemoteCertificateValidationCallback`
(NetCoreServer authenticates via `BeginAuthenticateAsServer`, so `CertificateChainPolicy` is not
available). Revocation mode SHALL default to `Offline` (cached CRL) to avoid handshake-time DoS; `NoCheck`
SHALL require an explicit justification.

#### Scenario: Untrusted issuer rejected

- **GIVEN** a secure server trusting only private CA `R`
- **WHEN** a client presents a certificate not chaining to `R` (self-signed or other CA)
- **THEN** validation returns false and the TLS handshake fails — RPC dispatch is never reached

#### Scenario: Valid client certificate accepted

- **GIVEN** a client certificate that chains to `R`, is in its validity period, and carries the
  `clientAuth` EKU
- **WHEN** the handshake runs
- **THEN** validation succeeds and the connection is established

#### Scenario: SPKI pin allowlist enforced

- **GIVEN** a validator configured with an SPKI-SHA-256 pin allowlist
- **WHEN** a CA-valid certificate whose SPKI is not in the allowlist is presented
- **THEN** validation returns false (defense-in-depth beyond CA trust)

### Requirement: A validated certificate SHALL be resolved to a node identity exposed on the session

A validated client certificate SHALL be resolved by an `INodeIdentityResolver` to a `NodeIdentity`
(SAN URI / SPIFFE id as the principal name, SPKI-SHA-256 as the stable fallback id) and exposed as a
`ClaimsPrincipal` on `AbstractJsonRpcSession` (`protected ClaimsPrincipal? Principal { get; }`), set
before `RegisterServices`/`StartListening`. The resolver SHALL be replaceable so a consumer can derive
identity from a different field (e.g. OU) without forking the framework.

#### Scenario: SAN URI becomes the identity

- **GIVEN** a valid client certificate with SAN URI `spiffe://example.org/service/billing`
- **WHEN** the session is established
- **THEN** the session `Principal` name is `spiffe://example.org/service/billing`

#### Scenario: SPKI fallback when no SAN

- **GIVEN** a valid client certificate with no SAN URI
- **WHEN** the identity is resolved
- **THEN** the `NodeIdentity` stable id is the certificate's SPKI-SHA-256 thumbprint
