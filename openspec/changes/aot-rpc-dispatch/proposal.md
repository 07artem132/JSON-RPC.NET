# aot-rpc-dispatch — source-generated AddLocalRpcMethod for Native-AOT RPC dispatch → 2.5.0

## Why

`aot-readiness` (2.4.0) made service **discovery** Native-AOT-clean, but RPC **dispatch** still goes through
StreamJsonRpc's `JsonRpc.AddLocalRpcTarget(object, …)` in `AbstractRpcServiceRegistry.RegisterServices` —
which is `[RequiresDynamicCode]` + `[RequiresUnreferencedCode]` (reflects over the target type at runtime).
The spike found the AOT-clean alternative: `JsonRpc.AddLocalRpcMethod(string, Delegate)` carries **no** AOT
attributes. This change source-generates one `AddLocalRpcMethod` call per RPC-interface method, binding a
compile-time delegate (method group) — no runtime type reflection.

## What Changes

| Area | Change |
|---|---|
| Runtime (lib) | New `IRpcMethodBinder { void Bind(JsonRpc, IServiceProvider, Guid clientId); }`. `RegisterServices` prefers an injected binder (AOT dispatch path) and otherwise falls back to the existing `AddLocalRpcTarget` reflection path (now extracted to `RegisterServicesViaReflection`, annotated `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]`, suppressed at the boundary). |
| Generator | Emits `RpcMethodBinder : IRpcMethodBinder` + a **separate** `AddGeneratedRpcMethodBinder(this IServiceCollection)` extension. Per RPC-interface method it emits `jsonRpc.AddLocalRpcMethod("<name>", new Func<…>/Action<…>(svc.Method))`. Name = camelCase (matching `CommonMethodNameTransforms.CamelCase`) unless `[JsonRpcMethod("…")]` overrides; `[JsonRpcIgnore]` methods are skipped. Regular services resolve from DI; client-aware services are constructed (single public ctor: `Guid`→`clientId`, others→`GetRequiredService`). Unsupported method shapes (generic, `ref`/`out`/`in`, >16 params) → diagnostic `WSRPC002`, skipped. |

### Opt-in & compatibility (deliberate)
The binder is a **separate** opt-in from the catalog. `AddGeneratedRpcServiceCatalog()` (discovery) is
unchanged; AOT dispatch requires the consumer to **also** call `AddGeneratedRpcMethodBinder()`. This means
existing 2.3.0/2.4.0 catalog users get **no behavior change** — they keep `AddLocalRpcTarget` dispatch
unless they explicitly opt into the binder. Minor bump (2.5.0): purely additive.

### Behavior trade-offs of the binder path (documented)
The delegate path is **not** a 1:1 replacement for `AddLocalRpcTarget`. It exposes only the **interface's**
methods (not impl-only public methods), and drops `AddLocalRpcTarget`-specific features: events on the
target, `RpcMarshalable` objects, `JsonRpcTargetOptions` (e.g. `AllowNonPublicInvocation`), and target
`IProgress<T>` marshalling beyond what a plain delegate parameter supports. Consumers needing those keep
the reflection path (don't register the binder). For typed request/response RPC (the common case) the
binder is equivalent.

## Capabilities

### `aot-rpc-dispatch`
When a consumer registers the generated `IRpcMethodBinder` (`AddGeneratedRpcMethodBinder()`),
`RegisterServices` SHALL bind every RPC-interface method via `JsonRpc.AddLocalRpcMethod(name, delegate)`
with **no** runtime type reflection, honoring `[JsonRpcMethod]`/`[JsonRpcIgnore]` and the camelCase name
transform. Without a binder, dispatch SHALL fall back to the reflection `AddLocalRpcTarget` path unchanged.

Guards: a generator test (emitted binder lists the expected `AddLocalRpcMethod` calls with correct names +
delegate types, honors the attributes, and compiles against real StreamJsonRpc); a runtime test (registry
with an injected binder calls it and skips the reflection scan); and the `aot-smoke` native binary extended
to register methods via the binder on a real `JsonRpc` under `PublishAot` and exercise a dispatch.
