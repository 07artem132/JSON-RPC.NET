# Tasks — composition-and-config

## config-validation (M5)
- [x] Add `[Range]`/`[Required]` DataAnnotations to `JsonRpcServerConfig` constrained fields.
- [x] Add source-gen `[OptionsValidator]` validator `JsonRpcServerConfigValidator` (reflection-free).
- [x] Add direct `Microsoft.Extensions.Options` 9.0.3 reference (so the source generator runs).
- [x] Wire config through `AddOptionsWithValidateOnStart` + `.Configure` + `.Validate` (TimeSpan rule)
      + register the validator via `TryAddEnumerable`; keep `JsonRpcServerConfig` directly resolvable.
- [x] Regression guard `JsonRpcServerConfigValidationTests` — per-field invalid values throw
      `OptionsValidationException`; default + valid custom config pass.

## composition-root-complete (H1)
- [x] Add generic overload `AddJsonRpcCore<TServer,TSession,TEventProcessor,TSubscriptionManager,TRegistry>`.
- [x] Register the five core services (one-instance-two-roles for processor + subscription manager) and
      build the concrete server from validated config via `ActivatorUtilities.CreateInstance<TServer>`.
- [x] Idempotency: private sentinel marker on the base overload + `TryAdd*` everywhere.
- [x] Update `example/SimpleServer/Program.cs` to the single-call composition root (remove boilerplate).
- [x] Regression guard `AddJsonRpcCoreCompositionTests` — all services resolvable, correct lifetimes,
      one-instance-two-roles, repeated registration idempotent.

## Close-out
- [x] `dotnet build` 0 warnings (audit on) + `dotnet test` green (90 → 112).
- [x] Version bump `1.2.0 → 1.3.0` in `Directory.Build.props`.
- [x] Doc-sync: `AUDIT-FINDINGS.md` (H1/M5 shipped), `CLAUDE.md` (rule 6 shipped, backlog, test count),
      `.claude/rules/audit-debt.md` (table), `.claude/rules/patterns.md` (composition root + config).
