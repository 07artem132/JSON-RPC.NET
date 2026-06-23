# registry-sourcegen-discovery — compile-time RPC service discovery (AOT-honest) → 2.3.0

## Why

`AbstractRpcServiceRegistry` discovers `IRpcService` implementations at runtime via
`AppDomain.CurrentDomain.GetAssemblies()` + `assembly.GetExportedTypes()` + `IsAssignableFrom`. That scan
is the last open audit finding (rule #4 / H3 follow-up): it is **not trim/AOT-compatible** (IL2026 /
IL3050), so the project cannot honestly advertise `<IsAotCompatible>true</IsAotCompatible>`.

This change adds a **Roslyn source generator** that performs the same discovery at **compile time** in the
consumer's assembly and emits a reflection-free catalog. The runtime registry prefers an injected catalog
and only falls back to the reflection scan when none is present — so existing consumers keep working
unchanged, and AOT-minded consumers opt in with one assembly attribute + one DI call.

### Honest scope (what this does and does not unlock)
This removes the reflection from **our** service-discovery code. It does **not** make the whole library
`IsAotCompatible`: StreamJsonRpc's `JsonRpc.AddLocalRpcTarget(...)` (used in `RegisterServices`) is itself
reflection / dynamic-proxy based, so full AOT remains blocked upstream. We therefore do **not** flip
`<IsAotCompatible>true</IsAotCompatible>` in this change; we close the part we own and document the
remaining external blocker. Rule #4's guard ("don't set IsAotCompatible without a source-gen alternative")
now has its source-gen alternative.

## What Changes

| Area | Change |
|---|---|
| Runtime (lib) | New public `RpcServiceDescriptor` (record struct), `IRpcServiceCatalog`, and `[GenerateRpcServiceCatalog]` assembly attribute. `AbstractRpcServiceRegistry` resolves `IRpcServiceCatalog` from DI; if present, builds its type cache from the catalog (no reflection). The reflection scan is kept as a fallback and annotated `[RequiresUnreferencedCode]` / `[RequiresDynamicCode]`. |
| Generator | New `src/WsRpcServer.SourceGenerator` (`netstandard2.0`, `IIncrementalGenerator`). When a consumer compilation carries `[assembly: GenerateRpcServiceCatalog]`, it finds every non-abstract class implementing an interface that derives from `IRpcService`, classifies client-aware vs regular, and emits an internal `RpcServiceCatalog : IRpcServiceCatalog` plus a public `AddGeneratedRpcServiceCatalog(this IServiceCollection)` extension. |
| Packaging | The library packs the generator DLL into `analyzers/dotnet/cs` so NuGet consumers get it automatically. |

**Out of scope:** flipping `IsAotCompatible` (StreamJsonRpc-blocked); changing the default (reflection)
discovery for consumers who don't opt in.

## Capabilities

### `sourcegen-catalog`
A consumer that adds `[assembly: GenerateRpcServiceCatalog]` and calls `AddGeneratedRpcServiceCatalog()`
SHALL get a compile-time-generated `IRpcServiceCatalog` enumerating every `(interface, impl, isClientAware)`
triple for its `IRpcService` implementations, with **no** runtime assembly scan. `AbstractRpcServiceRegistry`
SHALL use an injected `IRpcServiceCatalog` when available and otherwise fall back to the reflection scan
(unchanged behavior). The reflection paths SHALL carry `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]`
so the catalog path is trim/AOT-clean.

Guards: a generator test (drive `CSharpGeneratorDriver` over a sample compilation, assert the emitted
catalog lists the expected descriptors and compiles) and a runtime test (registry with an injected catalog
builds its cache from the catalog and never invokes the reflection scan).
