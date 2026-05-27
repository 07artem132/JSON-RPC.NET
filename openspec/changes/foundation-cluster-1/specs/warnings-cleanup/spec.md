# Spec — warnings-cleanup

## ADDED Requirements

### Requirement: `dotnet build` SHALL produce 0 warnings across src + tests

`dotnet build src/WsRpcServer/WsRpcServer.csproj tests/WsRpcServer.Tests/WsRpcServer.Tests.csproj` SHALL emit zero analyzer warnings post-merge. Baseline (audit-time, commit `6c9cb6d`): **108 warnings**. Post-cleanup target: **0**.

Warnings cluster breakdown:
- **~90 CS86xx (nullability)** у Moq-based test infrastructure: CS8602 (dereference of possibly-null reference), CS8605 (unboxing possibly-null value), CS8620 (argument type nullability mismatch для `Func<It.IsAnyType, Exception, string>` vs framework's `Func<..., Exception?, ...>`).
- **2 xUnit1031** у `tests/WsRpcServer.Tests/Core/AbstractSubscriptionStoreTests.cs:515,556` — `Task.WaitAll(tasks)` всередині `[Fact]`-method (sync-blocking деletes-vector).
- **1 CS0168** unused `ex` variable у `tests/WsRpcServer.Tests/Transport/WebSocketMessageHandlerTests.cs:455`.
- **~15 minor CS86xx** у remaining test files.

Усі warnings — **у tests/**; зніжодного не у src/. Source-side залишається untouched.

Fix patterns:
- CS8620 для Moq logger formatters: cast formatter argument to `It.IsAny<Func<It.IsAnyType, Exception?, string>>()` (nullable Exception?).
- CS8602/CS8605 де invariant established by setup: use null-forgiving `!` operator (post-`Assert.NotNull(...)` site).
- xUnit1031: replace `Task.WaitAll(...)` → `await Task.WhenAll(...)` + convert containing `[Fact]` method signature до `async Task`.
- CS0168 unused: delete variable declaration.

#### Scenario: Zero warnings across lib + tests

- **GIVEN** post-merge state
- **WHEN** `dotnet build src/WsRpcServer/WsRpcServer.csproj tests/WsRpcServer.Tests/WsRpcServer.Tests.csproj` runs (без `-p:TreatWarningsAsErrors=true`)
- **THEN** output contains "**0 Warning(s)**"
- **AND** output contains "**0 Error(s)**"
- **AND** exit code is 0

#### Scenario: Test count preserved

- **GIVEN** pre-cleanup `dotnet test` produces 83 passing
- **WHEN** post-cleanup `dotnet test` runs
- **THEN** result is also 83 passing (no test logic touched)

#### Scenario: xUnit1031 sites converted to async-await

- **GIVEN** previously-blocking `[Fact]` methods at `AbstractSubscriptionStoreTests.cs:515,556`
- **WHEN** files inspected post-cleanup
- **THEN** signatures are `async Task` (not `void` chi `Task`-without-async)
- **AND** body contains `await Task.WhenAll(...)` (not `Task.WaitAll`)
