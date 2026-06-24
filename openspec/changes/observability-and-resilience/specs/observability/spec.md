# Spec — observability

## ADDED Requirements

### Requirement: The library SHALL expose a Meter and ActivitySource named "WsRpcServer"

The library SHALL publish a `Meter` named `"WsRpcServer"` carrying `wsrpc.connections.active`
(UpDownCounter), `wsrpc.connections.rejected` (Counter), `wsrpc.notifications` (Counter with a `result`
tag), `wsrpc.parse_failures` (Counter), and `wsrpc.authorization.denied` (Counter); and an
`ActivitySource` named `"WsRpcServer"` emitting a per-connection lifecycle span. Instrumentation SHALL be
inert when no listener is attached.

#### Scenario: Connection gauge tracks active connections

- **GIVEN** a `MeterListener` subscribed to `wsrpc.connections.active`
- **WHEN** a connection is accepted and later torn down
- **THEN** the gauge records `+1` on accept and `-1` on teardown

#### Scenario: Notification result is tagged

- **GIVEN** a `MeterListener` subscribed to `wsrpc.notifications`
- **WHEN** a notification is queued and another is dropped (channel full)
- **THEN** measurements carry `result=queued` and `result=dropped` respectively

### Requirement: Metric tags SHALL be limited to a known privacy-safe set

Recorded measurement tag keys SHALL be a subset of a fixed allowlist (`result`). No tag value SHALL carry
message bodies, identity secrets, or other PII — only enum-like literals, statuses, and counts.

#### Scenario: No unknown tag keys

- **GIVEN** a `MeterListener` capturing all `WsRpcServer` instruments
- **WHEN** notifications, parse failures, rejections, and auth denials are recorded
- **THEN** every captured tag key is within the allowlist `{ "result" }`
