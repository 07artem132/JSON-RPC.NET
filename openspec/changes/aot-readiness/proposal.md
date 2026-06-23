# aot-readiness — make the discovery path provably Native-AOT-clean → 2.4.0

## Why

The `registry-sourcegen-discovery` build-spike (with `-p:IsAotCompatible=true`) showed the library's **only**
trim/AOT warnings are our own and all mechanically fixable — **none** come from StreamJsonRpc or NetCoreServer
at our compile boundary:

- `IL2091` ×5 — `AddJsonRpcCore<…>` passes its generic type params to `ActivatorUtilities.CreateInstance<T>` /
  `TryAdd*<T>` without `[DynamicallyAccessedMembers]`.
- `IL2026`/`IL3050`/`IL3000` — the reflection fallback in `AbstractRpcServiceRegistry` (already annotated
  `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]`) is reached from the non-annotated dispatcher.

This change clears those so a consumer who opts into the source-gen catalog (`registry-sourcegen-discovery`)
has a **provably Native-AOT-clean discovery path**, demonstrated by a real `dotnet publish -p:PublishAot=true`
native binary that runs the catalog-based registry with zero reflection.

### Honest scope boundary
This makes service **discovery** AOT-native. It does **not** make RPC **dispatch** AOT-native: the dispatch
still goes through StreamJsonRpc's reflection-based `JsonRpc.AddLocalRpcTarget` in `RegisterServices`. The
spike found the AOT-clean alternative — source-generated `JsonRpc.AddLocalRpcMethod(name, delegate)` (no
`[RequiresDynamicCode]`/`[RequiresUnreferencedCode]` on that overload) — but swapping the framework's dispatch
mechanism is behavior-changing (loses some `AddLocalRpcTarget` features: events, IProgress, marshalable
objects) and is deferred to its own capability (`aot-rpc-dispatch`) with explicit trade-off sign-off. We do
**not** set `<IsAotCompatible>true</IsAotCompatible>` on the library yet for the same reason.

## What Changes

| Area | Change |
|---|---|
| `JsonRpcCoreExtensions` | `[DynamicallyAccessedMembers(PublicConstructors)]` on `TServer`/`TSession`/`TEventProcessor`/`TSubscriptionManager`/`TRegistry` → clears IL2091. |
| `AbstractRpcServiceRegistry` | Honest `[UnconditionalSuppressMessage]` at the dispatcher→reflection-fallback boundary (IL2026/IL3050) + IL3000 on `Assembly.Location`, justified by "catalog is the AOT path; reflection is the documented opt-out". |
| Proof | New `aot-smoke/` console app: `[assembly: GenerateRpcServiceCatalog]` + `AddGeneratedRpcServiceCatalog()` + builds the registry cache from the catalog. Published with `PublishAot=true`, run as a native binary, asserts discovery works with no reflection. |
| Verify | Re-run `-p:IsAotCompatible=true` on the lib → expect **0** IL warnings. |

## Capabilities

### `aot-readiness`
`AddJsonRpcCore<…>`'s generic parameters SHALL carry `[DynamicallyAccessedMembers(PublicConstructors)]` so a
trim/AOT-publishing consumer gets no IL2091 for the DI registrations. The reflection fallback boundary in
`AbstractRpcServiceRegistry` SHALL be suppressed with a justification that points to the source-gen catalog as
the AOT path. With these, `-p:IsAotCompatible=true` on `src/WsRpcServer` SHALL produce 0 IL warnings. A native
`PublishAot` smoke binary SHALL prove the catalog-based discovery path compiles to native and runs.
