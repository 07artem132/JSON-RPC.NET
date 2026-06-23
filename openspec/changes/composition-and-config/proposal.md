# composition-and-config — complete the DI composition root + validate config → 1.3.0

## Why

`AUDIT-FINDINGS.md` lists two adjacent ergonomics/correctness findings that are the next-lowest-risk
items after the `security-hardening` cluster shipped (per the roadmap "ordered low-risk first:
`config-validation` (M5) → `composition-root-complete` (H1)"):

- **M5** — `JsonRpcServerConfig` has no `[Range]`/`[Required]` validation. A bad `Port` (0 / 65536),
  empty `Host`, or non-positive `NotificationQueueSize` is accepted silently and only fails deep inside
  channel/socket construction at runtime, far from the misconfiguration.
- **H1** — `AddJsonRpcCore` registers **only** the config; the consumer must hand-wire five services
  (`IEventProcessor`, `ISubscriptionManager`, `IRpcServiceRegistry`, the concrete server + session) and
  construct the server manually (17 lines of boilerplate leaked into `example/SimpleServer/Program.cs`).
  There is also no idempotency guard — repeated calls silently duplicate the config registration.

The two pair naturally: the completed composition root is exactly where the validated options pipeline
is wired. Each capability ships with a regression-guard test (per
`.claude/rules/audit-debt.md` — "a fix without a guard is the next regression").

## What Changes

| # | Capability | Finding | Files | Risk |
|---|---|---|---|---|
| 1 | `config-validation` | M5 | `JsonRpcServerConfig.cs`, new `JsonRpcServerConfigValidator.cs`, `JsonRpcCoreExtensions.cs`, `WsRpcServer.csproj` | low |
| 2 | `composition-root-complete` | H1 | `JsonRpcCoreExtensions.cs`, `example/SimpleServer/Program.cs` | low |

**Out of scope:** M2/M3/M4 (subscription API shape — a breaking interface redesign), the
`logger-message-migration` (CA1848/CA1873, ~190 call sites), and the registry AOT source-gen
alternative. These remain open in `AUDIT-FINDINGS.md`.

## Capabilities

### `config-validation` (M5)
`JsonRpcServerConfig` SHALL carry DataAnnotations (`[Range]`/`[Required]`) on every constrained field
(`Host`, `Port`, `MaxMessageSizeBytes`, `NotificationQueueSize`, `PipeThresholdBytes`,
`MaxConsecutiveParseFailures`). A source-generated `[OptionsValidator]` validator
(`JsonRpcServerConfigValidator`, reflection-free / AOT-safe) SHALL be wired through the options pipeline
in `AddJsonRpcCore`, plus a cross-field `.Validate(...)` rule for the non-annotatable `TimeSpan`
`NotificationTimeout` (must be positive). Invalid configuration SHALL surface as
`OptionsValidationException` when the options/config are resolved (fail-fast), not as an opaque failure
deep in channel/socket construction.

### `composition-root-complete` (H1)
A generic-parameterised overload
`AddJsonRpcCore<TServer, TSession, TEventProcessor, TSubscriptionManager, TRegistry>(...)` SHALL register
all five core services plus construct the concrete server from validated config (`Host` → `IPAddress`),
so consumers no longer hand-wire them. The event processor and subscription manager SHALL follow the
"one instance, two roles" pattern (concrete singleton + interface resolving to the same instance). All
registrations SHALL be idempotent (`TryAdd*` + a private sentinel marker), so repeated `AddJsonRpcCore`
calls are a no-op and never duplicate the config registration.

## Verification

- `dotnet build JSON-RPC.NET.sln -c Release` — 0 warnings, NuGet audit on.
- `dotnet test` — existing suite + new guard tests green (90 → 112).
- Version bump `1.2.0 → 1.3.0` in `Directory.Build.props`; `AUDIT-FINDINGS.md` + `CLAUDE.md` move
  H1/M5 to a shipped state; `example/SimpleServer/Program.cs` boilerplate removed.
