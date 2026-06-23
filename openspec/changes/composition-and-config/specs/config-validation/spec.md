# Spec — config-validation

## ADDED Requirements

### Requirement: JsonRpcServerConfig SHALL be validated fail-fast through the options pipeline

`JsonRpcServerConfig` SHALL declare DataAnnotations on every constrained field: `Host`
(`[Required(AllowEmptyStrings = false)]`), `Port` (`[Range(1, 65535)]`), `MaxMessageSizeBytes`,
`NotificationQueueSize`, `PipeThresholdBytes`, `MaxConsecutiveParseFailures` (each `[Range(1, int.MaxValue)]`).
A source-generated `[OptionsValidator]` validator (`JsonRpcServerConfigValidator`, reflection-free) SHALL
be registered through `AddJsonRpcCore`, together with a cross-field `.Validate(...)` rule requiring
`NotificationTimeout` to be strictly positive (it is a `TimeSpan`, so no DataAnnotation applies).
Invalid configuration SHALL surface as `OptionsValidationException` when the options or the
directly-resolved `JsonRpcServerConfig` are obtained.

#### Scenario: Invalid port is rejected

- **GIVEN** `services.AddJsonRpcCore(c => c.Port = 0)` (or `65536`, or a negative value)
- **WHEN** `IOptions<JsonRpcServerConfig>.Value` (or `JsonRpcServerConfig`) is resolved
- **THEN** an `OptionsValidationException` is thrown

#### Scenario: Empty host is rejected

- **GIVEN** `services.AddJsonRpcCore(c => c.Host = "")` (or `null`)
- **WHEN** the config is resolved
- **THEN** an `OptionsValidationException` is thrown

#### Scenario: Non-positive queue/buffer/counter/timeout is rejected

- **GIVEN** a config with `NotificationQueueSize`, `MaxMessageSizeBytes`, `PipeThresholdBytes`, or
  `MaxConsecutiveParseFailures` ≤ 0, or `NotificationTimeout` ≤ `TimeSpan.Zero`
- **WHEN** the config is resolved
- **THEN** an `OptionsValidationException` is thrown

#### Scenario: Default and valid custom config pass

- **GIVEN** the default `JsonRpcServerConfig`, or a valid custom one (e.g. `Host=127.0.0.1`, `Port=8443`,
  all ranges satisfied)
- **WHEN** the config is resolved
- **THEN** no exception is thrown and the configured values are returned
