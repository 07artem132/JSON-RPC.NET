# Tasks — foundation-cluster-1 (→ 1.1.0)

4 capabilities, ordered low-risk first. Each lands у власному commit'і. Final commit bundles version bump (`0.1.0 → 1.1.0`) + (optional) CHANGELOG seed.

## 0. Setup

- [ ] 0.1 `npx -y @fission-ai/openspec@latest validate foundation-cluster-1 --strict` — confirm green перед будь-якими source-edit'ами.
- [ ] 0.2 Зафіксувати baseline: `dotnet build` → 108 warnings; `dotnet test` → 83 passing.

## Capability 1 — `readme-org-fix`

- [x] 1.1 Edit `README.md:7` — build-status badge URL: replace `mil-development` substring with `07artem132` (2 occurrences у tag).
- [x] 1.2 Edit `README.md:60` — NuGet feed URL: `nuget.pkg.github.com/mil-development/index.json` → `nuget.pkg.github.com/07artem132/index.json`.
- [x] 1.3 `grep -c 'mil-development' README.md` — assert 0.
- [x] 1.4 Commit: `docs(readme): fix GitHub org references (mil-development → 07artem132)`.

## Capability 2 — `directory-build-props`

- [x] 2.1 Create `Directory.Build.props` у repo root з:
  ```xml
  <Project>
    <PropertyGroup>
      <AnalysisLevel>latest-recommended</AnalysisLevel>
      <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
      <Nullable>enable</Nullable>
      <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
      <WsRpcServerPackageVersion>1.1.0</WsRpcServerPackageVersion>
    </PropertyGroup>
  </Project>
  ```
- [x] 2.2 Edit `src/WsRpcServer/WsRpcServer.csproj` — replace hardcoded `<Version>` (якщо є) на `<Version>$(WsRpcServerPackageVersion)</Version>` + `<AssemblyVersion>$(WsRpcServerPackageVersion)</AssemblyVersion>` + `<FileVersion>$(WsRpcServerPackageVersion)</FileVersion>`.
- [x] 2.3 Remove per-csproj `<Nullable>enable</Nullable>` (тепер inherited from Directory.Build.props): src/WsRpcServer.csproj, tests/WsRpcServer.Tests.csproj, example/SimpleServer.csproj, example/SimpleClient.csproj.
- [x] 2.4 `dotnet build` — 0 errors, ≤108 warnings (no change expected; build-props is purely organizational).
- [x] 2.5 `dotnet test --no-build` — 83 passing.
- [x] 2.6 Commit: `chore(msbuild): centralize props у Directory.Build.props + version property`.

## Capability 3 — `warnings-cleanup`

⚠ Цей крок наймасштабніший у cluster'і — ~108 fixes у 5 test files. Split на sub-commit'и по test-file якщо стане надто великим.

- [ ] 3.1 Fix `tests/WsRpcServer.Tests/Transport/WebSocketMessageHandlerTests.cs` (~70 warnings):
  - Moq logger formatter type mismatches (CS8620): use `It.IsAny<Func<It.IsAnyType, Exception?, string>>()` (nullable Exception?).
  - Box-unbox nulls (CS8602/CS8605): explicit non-null assertion (`!`) where invariant established by setup.
  - Unused `ex` (CS0168) line 455: delete variable declaration.
- [ ] 3.2 Fix `tests/WsRpcServer.Tests/Sessions/TestJsonRpcSession.cs` line 490 (CS8620): correct Moq formatter signature.
- [ ] 3.3 Fix `tests/WsRpcServer.Tests/Core/AbstractSubscriptionStoreTests.cs:515,556` (xUnit1031): replace `Task.WaitAll(tasks)` → `await Task.WhenAll(tasks)`. Convert containing `[Fact]` method to `async Task` return.
- [ ] 3.4 Fix remaining files (~15 warnings): `Events/TestEventProcessor.cs`, `Exceptions/RpcErrorExceptionTests.cs`, `Subscriptions/TestSubscriptionManager.cs`.
- [ ] 3.5 `dotnet build` — 0 warnings.
- [ ] 3.6 `dotnet test --no-build` — 83 passing (no test logic touched, only annotations/awaits).
- [ ] 3.7 Commit: `test(hygiene): fix all 108 build warnings (nullability + xUnit1031 + unused-var)`.

## Capability 4 — `treat-warnings-errors`

- [ ] 4.1 Edit `src/WsRpcServer/WsRpcServer.csproj` — add `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` у `<PropertyGroup>`.
- [ ] 4.2 Edit `tests/WsRpcServer.Tests/WsRpcServer.Tests.csproj` — same.
- [ ] 4.3 (NOT edit `example/*.csproj` — consumer code залишається lax.)
- [ ] 4.4 `dotnet build` — 0 errors, 0 warnings (confirmation finalized після #3).
- [ ] 4.5 `dotnet test --no-build` — 83 passing.
- [ ] 4.6 Negative-case sanity: temporarily introduce `int unused;` у будь-якому src file → `dotnet build` MUST fail з error CS0219 (not warning). Revert.
- [ ] 4.7 Commit: `chore(build): enable TreatWarningsAsErrors on lib + tests`.

## Release commit (final)

- [ ] 5.1 `dotnet build` final pass; `dotnet test` — 83 passing.
- [ ] 5.2 `npx -y @fission-ai/openspec@latest validate foundation-cluster-1 --strict` — re-confirm green.
- [ ] 5.3 (Optional) Initialize `CHANGELOG.md` з 1.1.0 section + 4 capability bullets per `.claude/rules/openspec-workflow.md § CHANGELOG voice template` (TBD коли скажелон lands у `claude-md-scaffold` change). До тих пір — CHANGELOG-less release acceptable.
- [ ] 5.4 (Post-merge, separate workflow): archive change + record у future CLAUDE.md "Implemented, merged, archived" list.

## Notes

- **No version bump until 2.1.** Кожен individual capability commit non-version-changing — version moves один раз у `directory-build-props`. Partial merge можна — version залишиться `0.1.0` до Cap 2.
- **Test count delta:** zero. 83 → 83. Усі fixes — annotations / async-conversions без changing assertion semantics.
- **Build-warnings delta:** 108 → 0. Verified at Cap 3 end + sanity-pinned via Cap 4 `TreatWarningsAsErrors`.
- **Public-API delta:** zero. Жодного source-side type/method modification у `src/WsRpcServer/`.
