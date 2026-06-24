# Spec — connection-resilience

## ADDED Requirements

### Requirement: A concurrent-connection quota SHALL bound active connections

`JsonRpcServerConfig` SHALL carry `MaxConcurrentConnections` (`[Range(0, int.MaxValue)]`, default `0`).
When `> 0` and the live connection count exceeds the limit, the server SHALL reject the new connection
(disconnect it), log a Warning, and increment `wsrpc.connections.rejected` — the rejected connection SHALL
NOT reach RPC dispatch. A value of `0` SHALL preserve the current unbounded behavior (additive).

#### Scenario: Connection over the limit is rejected

- **GIVEN** `MaxConcurrentConnections = N` and `N` live connections
- **WHEN** an `(N+1)`-th connection is accepted
- **THEN** it is disconnected, a Warning is logged, and `wsrpc.connections.rejected` increments by 1

#### Scenario: Default is unbounded

- **GIVEN** `MaxConcurrentConnections = 0` (default)
- **WHEN** connections are accepted
- **THEN** none are rejected by the quota (behavior unchanged from before this change)
