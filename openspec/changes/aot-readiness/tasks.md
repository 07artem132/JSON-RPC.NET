# Tasks — aot-readiness

## Library fixes

- [x] `JsonRpcCoreExtensions`: add `using System.Diagnostics.CodeAnalysis;` + `[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]` on `TServer`, `TSession`, `TEventProcessor`, `TSubscriptionManager`, `TRegistry`.
- [x] `AbstractRpcServiceRegistry`: `[UnconditionalSuppressMessage]` for IL2026 + IL3050 on the `BuildServiceTypeCache` dispatcher (justify: catalog is the AOT path, reflection is opt-out fallback) + IL3000 on the `Assembly.Location` use.
- [x] Re-measure `dotnet build -p:IsAotCompatible=true -p:TreatWarningsAsErrors=false` → 0 IL warnings.

## Native-AOT proof

- [x] `aot-smoke/AotSmoke.csproj` (`Exe`, `PublishAot=true`) referencing lib + generator(analyzer), `[assembly: GenerateRpcServiceCatalog]`, sample `IRpcService` impls, builds registry cache from generated catalog, prints discovered count.
- [x] `dotnet publish aot-smoke -r linux-x64 -p:PublishAot=true` → native binary; run it; capture output proving discovery works (exit 0, no reflection).

## Verify + ship

- [x] `dotnet build` (normal) 0 warnings + `dotnet test` green (125).
- [x] Doc-sync: `CLAUDE.md` (rule #4 nuance: discovery AOT-clean, dispatch deferred), `AUDIT-FINDINGS.md`, `.claude/rules/{patterns,csproj-build}.md`.
- [x] Version bump 2.3.0 → 2.4.0; commit + push.
