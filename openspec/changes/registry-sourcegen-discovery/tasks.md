# Tasks — registry-sourcegen-discovery

## Runtime (library)

- [x] `Services/RpcServiceDescriptor.cs` — public `readonly record struct (Type InterfaceType, Type ImplementationType, bool IsClientAware)`.
- [x] `Services/IRpcServiceCatalog.cs` — public interface `IReadOnlyList<RpcServiceDescriptor> Services { get; }`.
- [x] `Services/GenerateRpcServiceCatalogAttribute.cs` — `[AttributeUsage(Assembly)]` opt-in marker.
- [x] `AbstractRpcServiceRegistry`: resolve `IRpcServiceCatalog` from `ServiceProvider`; if present build cache from it (no reflection); else reflection fallback. Annotate reflection methods `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]`.

## Source generator

- [x] `src/WsRpcServer.SourceGenerator/WsRpcServer.SourceGenerator.csproj` — `netstandard2.0`, `Microsoft.CodeAnalysis.CSharp` (PrivateAssets=all), `IsRoslynComponent`.
- [x] `RpcServiceCatalogGenerator : IIncrementalGenerator` — gate on `[assembly: GenerateRpcServiceCatalog]`; collect classes implementing an `IRpcService`-derived interface; emit `RpcServiceCatalog` + `AddGeneratedRpcServiceCatalog`.
- [x] Add the project to the solution; pack its DLL into `analyzers/dotnet/cs` in `WsRpcServer.csproj`.

## Tests + docs

- [x] Generator test (`CSharpGeneratorDriver`) — sample compilation → emitted catalog has expected descriptors + compiles clean.
- [x] Runtime test — registry with injected catalog builds cache from it and does NOT scan assemblies.
- [x] `dotnet build` 0 warnings + `dotnet test` green.
- [x] Doc-sync: `CLAUDE.md`, `AUDIT-FINDINGS.md` (rule #4 source-gen alternative shipped; IsAotCompatible still blocked by StreamJsonRpc), `.claude/rules/{patterns,csproj-build,audit-debt}.md`.
- [x] Version bump 2.2.0 → 2.3.0.
