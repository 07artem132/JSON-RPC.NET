# Spec — rpc-authorization

## ADDED Requirements

### Requirement: Attributed RPC methods SHALL be authorized deny-by-default at dispatch

RPC interface methods MAY carry `[RpcAuthorize(Roles = …)]` (`AttributeUsage(Method | Interface)`). At
dispatch — on **both** the reflection `RegisterServices` path and the generated `IRpcMethodBinder` — an
`IRpcAuthorizationPolicy` SHALL evaluate the session's `ClaimsPrincipal` against the attribute's
requirement **before the method body runs**. The default `StaticRoleMapAuthorizationPolicy` SHALL map a
`NodeIdentity` to roles from consumer-supplied configuration. A principal lacking a required role SHALL
cause the invocation to fail with `RpcErrorException` (application code, e.g. `-32001`). Methods **without**
`[RpcAuthorize]` SHALL remain unrestricted (the capability is additive).

#### Scenario: Missing role denied before execution

- **GIVEN** an RPC method `[RpcAuthorize(Roles = "admin")]` and a session principal without `admin`
- **WHEN** the client invokes the method
- **THEN** the call fails with `RpcErrorException` (`-32001`) and the method body is never entered

#### Scenario: Sufficient role permitted

- **GIVEN** the same method and a session principal holding `admin`
- **WHEN** the client invokes the method
- **THEN** the method body executes and returns its result

#### Scenario: Unattributed method stays open

- **GIVEN** an RPC method with no `[RpcAuthorize]`
- **WHEN** any authenticated session invokes it
- **THEN** it executes as today (no authorization check)

### Requirement: Authorization SHALL hold on the AOT-clean binder path

When the generated `IRpcMethodBinder` is used, the generator SHALL emit the authorization check at the
head of the `AddLocalRpcMethod` delegate for each attributed method, reading `[RpcAuthorize]` at compile
time so the runtime check stays reflection-free (preserving the Native-AOT-clean dispatch property).

#### Scenario: Binder enforces the same policy as reflection

- **GIVEN** a consumer using `AddGeneratedRpcMethodBinder()` with an `[RpcAuthorize]` method
- **WHEN** a principal lacking the role invokes it
- **THEN** the binder-generated delegate denies with `RpcErrorException` (`-32001`) before invoking the
  service method — identical to the reflection path
