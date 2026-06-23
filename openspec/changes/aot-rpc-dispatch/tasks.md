# Tasks — aot-rpc-dispatch

## Runtime (library)

- [x] `Services/IRpcMethodBinder.cs` — `void Bind(JsonRpc jsonRpc, IServiceProvider sp, Guid clientId)`.
- [x] `AbstractRpcServiceRegistry.RegisterServices`: prefer injected `IRpcMethodBinder`; else
      `RegisterServicesViaReflection` (extracted, `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]`,
      suppressed at the dispatcher). Add `BinderUsed` log.

## Generator

- [x] Emit `RpcMethodBinder : IRpcMethodBinder` + `AddGeneratedRpcMethodBinder(this IServiceCollection)`.
- [x] Per interface method: camelCase name (or `[JsonRpcMethod]` override), skip `[JsonRpcIgnore]`,
      `AddLocalRpcMethod("name", new Func<…>/Action<…>(svc.Method))`.
- [x] Regular svc → DI resolve; client-aware → single-ctor construct (`Guid`→clientId, else GetRequiredService).
- [x] Unsupported method shapes (generic/ref/out/in/>16 params) → diagnostic `WSRPC002`, skip.

## Tests + proof

- [x] Generator test: binder content (names, delegate types, attribute handling) + compiles vs real StreamJsonRpc.
- [x] Runtime test: registry with injected binder calls it, skips reflection scan.
- [x] Extend `aot-smoke`: register via binder on a real `JsonRpc` over an in-memory stream under PublishAot;
      run native binary; prove dispatch works (exit 0).

## Ship

- [x] `dotnet build` 0 warnings + `dotnet test` green; re-measure `-p:IsAotCompatible=true` (binder path).
- [x] Doc-sync: `CLAUDE.md` (rule #4 → dispatch shipped, trade-offs), `AUDIT-FINDINGS.md`, `.claude/rules/patterns.md`.
- [x] Version bump 2.4.0 → 2.5.0; commit + push.
