# Spec — directory-build-props

## ADDED Requirements

### Requirement: Repo SHALL define shared MSBuild canon у Directory.Build.props

`Directory.Build.props` SHALL exist at repo root (auto-imported by MSBuild у every `*.csproj` build, per [MS Learn](https://learn.microsoft.com/visualstudio/msbuild/customize-by-directory)). Properties declared:

- `<AnalysisLevel>latest-recommended</AnalysisLevel>` — opts up roslyn analyzer pack to latest recommended rule set.
- `<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>` — `dotnet format` violations become build errors.
- `<Nullable>enable</Nullable>` — repository-wide NRT context.
- `<TreatWarningsAsErrors>false</TreatWarningsAsErrors>` — default; per-csproj override у `treat-warnings-errors` capability (only `src/WsRpcServer.csproj` + tests opt in; example/* залишаються lax).
- `<WsRpcServerPackageVersion>0.2.0</WsRpcServerPackageVersion>` — single source of truth для main NuGet package version. Per-csproj `<Version>` references it via `$(WsRpcServerPackageVersion)`.

Per-csproj `<Nullable>enable</Nullable>` declarations SHALL be removed з `src/WsRpcServer.csproj`, `tests/WsRpcServer.Tests.csproj`, `example/SimpleServer.csproj`, `example/SimpleClient.csproj` (inherited from props).

#### Scenario: Directory.Build.props exists у repo root

- **GIVEN** post-merge state
- **WHEN** `test -f Directory.Build.props` runs
- **THEN** file exists
- **AND** XML root element is `<Project>`
- **AND** containing `<PropertyGroup>` includes усі 5 declared properties

#### Scenario: Per-csproj Nullable declarations removed

- **GIVEN** будь-який `*.csproj` під src/, tests/, example/
- **WHEN** `grep -c '<Nullable>' <csproj>` runs
- **THEN** result is **0** (inherited from props, не duplicated)

#### Scenario: Version property single-source-of-truth

- **GIVEN** `src/WsRpcServer/WsRpcServer.csproj`
- **WHEN** extracted `<Version>` element value
- **THEN** value is `$(WsRpcServerPackageVersion)` — not hardcoded numeric string
- **AND** `Directory.Build.props` defines `WsRpcServerPackageVersion = 0.2.0`
