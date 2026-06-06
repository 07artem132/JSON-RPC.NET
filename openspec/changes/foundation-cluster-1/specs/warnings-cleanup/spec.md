# Spec — warnings-cleanup

## ADDED Requirements

### Requirement: `dotnet build` SHALL produce 0 warnings across src + tests

`dotnet build src/WsRpcServer/WsRpcServer.csproj tests/WsRpcServer.Tests/WsRpcServer.Tests.csproj` SHALL emit zero analyzer warnings post-merge. Baseline post-Cap 2 (Directory.Build.props з `AnalysisLevel=latest-recommended`): **439 warnings**. Original audit baseline (pre-AnalysisLevel-bump): **108**. Cap 3 cleanup target: **0**.

**Warnings cluster breakdown (post-AnalysisLevel=latest-recommended):**

| Code | Count | Disposition |
|---|---|---|
| CA1873 | 222 | **DEFER** — cache LoggerMessage delegate; addressed by future capability `logger-message-migration` (SignalCli.NET-style source-gen `[LoggerMessage]` partial methods refactor — 100+ call sites) |
| CA1848 | 190 | **DEFER** — same rule (use `LoggerMessage` source-gen); same future capability |
| CA1707 | 154 | **SUPPRESS у tests** — `Foo_Bar_Baz` test method naming is xUnit-standard convention (Arrange_Act_Assert pattern, descriptive failure messages). Add to test csproj `<NoWarn>` |
| CS8602 | 80 | **FIX** — dereference of possibly-null reference (real nullability bug) |
| CS8620 | 52 | **FIX** — Moq formatter nullability mismatch (real bug) |
| CA1051 | 40 | **FIX** — visible instance fields у tests; encapsulate or suppress per-test-class |
| CS8605 | 28 | **FIX** — unbox null (real bug) |
| CS8618 | 20 | **FIX** — non-nullable field not initialized |
| CA1861 | 16 | **FIX** — `static readonly` for array allocation (perf) |
| CS8625 | 8 | **FIX** — null → non-nullable |
| CS1570 | 8 | **FIX** — malformed XMLDoc |
| CA1852 | 8 | **FIX** — types can be `sealed` |
| CS8619 | 6 | **FIX** — nullability mismatch у value types |
| CA1816 | 6 | **FIX** — `Dispose` pattern (call `GC.SuppressFinalize`) |
| CA1001 | 6 | **FIX** — type owns IDisposable but не IDisposable itself |
| CA1822 | 4 | **FIX** — mark members static |
| CS8603/CS8600 | 4 | **FIX** — possible null return/assignment |
| CS0618 | 2 | **REVIEW** — use of obsolete API; verify intent |
| CS0169 | 2 | **FIX** — unused field |

**Deferred → 566 occurrences** (CA1848 + CA1873 + CA1707). Cap 3 actually-fixes ≈ **278 occurrences** (CS86xx + remaining CA-rules).

**Suppressions added у this capability:**
- `Directory.Build.props`: `<NoWarn>$(NoWarn);CA1848;CA1873</NoWarn>` з comment posiланням на planned `logger-message-migration` capability.
- `tests/WsRpcServer.Tests.csproj`: `<NoWarn>$(NoWarn);CA1707</NoWarn>` (test-method naming convention).

Fix patterns:
- CS8620 для Moq logger formatters: cast formatter argument to `It.IsAny<Func<It.IsAnyType, Exception?, string>>()` (nullable Exception?).
- CS8602/CS8605/CS8618/CS8619/CS8625 — null-forgiving `!` operator where invariant established by setup, OR proper null-check, OR initializer.
- CA1051 (visible fields) — convert to `{ get; init; }` properties OR `internal` access.
- CA1816 — add `GC.SuppressFinalize(this)` у Dispose() impl.
- CA1001 — implement `IDisposable` for types owning disposable resources.
- CA1852 — add `sealed` to leaf types.
- CA1861 — extract literal arrays into `private static readonly` fields.
- CA1822 — promote instance members не-touching-state to `static`.
- CS1570 — fix malformed XMLDoc (escape `<`/`>` etc.).
- CS0169 — delete unused field OR mark `[FieldOffset]`/intentional.
- CS0618 — review obsolete API usage; suppress with justification OR migrate.

**xUnit1031** (2 sites у `AbstractSubscriptionStoreTests.cs:515,556`) — already у count above as CS-codes? No — xUnit1031 was a separate xUnit analyzer warning. Verified during Cap 3 implementation that it still surfaces post-Cap-2. Replace `Task.WaitAll(...)` → `await Task.WhenAll(...)` + convert containing `[Fact]` to `async Task`.

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
