# Spec — treat-warnings-errors

## ADDED Requirements

### Requirement: src + tests csproj SHALL declare TreatWarningsAsErrors=true

`src/WsRpcServer/WsRpcServer.csproj` AND `tests/WsRpcServer.Tests/WsRpcServer.Tests.csproj` SHALL declare `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` у `<PropertyGroup>`. This activates **only after** `warnings-cleanup` capability lands (otherwise build breaks immediately).

Example projects (`example/SimpleServer/SimpleServer.csproj`, `example/SimpleClient/SimpleClient.csproj`) SHALL NOT carry this flag — вони intentionally lax because consumer-facing demos з educational intent дозволяють analyzer suggestions залишатись як warnings (consumer reads code, не builds CI).

#### Scenario: Build fails on new warning у lib

- **GIVEN** post-merge state with `TreatWarningsAsErrors=true` on lib
- **WHEN** developer вносить новий warning-triggering code у `src/WsRpcServer/**/*.cs` (e.g., unused variable)
- **THEN** `dotnet build src/WsRpcServer/WsRpcServer.csproj` fails з exit code 1
- **AND** output contains "**error**" (not "warning") для that diagnostic

#### Scenario: Build fails on new warning у tests

- **GIVEN** post-merge state with `TreatWarningsAsErrors=true` on tests
- **WHEN** developer adds new test з sync-blocking `Task.WaitAll`
- **THEN** `dotnet build tests/WsRpcServer.Tests/WsRpcServer.Tests.csproj` fails з xUnit1031 surfaced as error

#### Scenario: Examples залишаються lax

- **GIVEN** `example/SimpleServer/SimpleServer.csproj` post-merge
- **WHEN** developer-added warning exists у example code
- **THEN** `dotnet build example/SimpleServer/SimpleServer.csproj` succeeds (warning surfaced but not promoted to error)
- **AND** csproj does NOT contain `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`
