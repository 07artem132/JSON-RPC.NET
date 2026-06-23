---
paths:
  - "tests/**"
---

# Testing patterns

- Stack: **xUnit** + **Moq** + **coverlet**. `Using Include="Xunit"` is global in the test csproj.
- The test csproj opts into `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, so a new test that
  introduces an analyzer warning **fails the build**. The two warning classes to watch:
  - **`xUnit1031`** (`DoNotUseBlockingTaskOperationsInTestMethod`) — no `Task.WaitAll` / `.Result` /
    `.Wait()` / `.GetAwaiter().GetResult()` in a `[Fact]`. Use `await Task.WhenAll(...)` and make the
    test method `async Task`. (`foundation-cluster-1/warnings-cleanup` removed the two existing
    `Task.WaitAll` violations — don't reintroduce the pattern.) If a test genuinely must block, add
    `[SuppressMessage("xUnit", "xUnit1031", Justification="…")]`.
  - **CS86xx nullability** — Moq logger formatters / `It.IsAny<>` often produce nullable/non-nullable
    mismatches; fix with correctly-typed locals or the null-forgiving operator only where nullability is
    actually guaranteed.

## Regression-guard philosophy

This repo is being hardened against the findings in `AUDIT-FINDINGS.md`. When you fix a finding,
**prefer a small reflection-based or behavioral guard test over narrative discipline** — that test is
the durable artifact that prevents recurrence. Suggested guards (from the audit) when their finding is
addressed:

- `AddJsonRpcCore_RegistersAllCoreServices` — resolves all core services + asserts singleton lifetime +
  repeated registration is idempotent (finding H1).
- `Dispose_DuringInFlightNotification_CancelsAndWaits` — session disposed mid-notification drains the
  background task without `ObjectDisposedException` in logs (finding H4).
- A `Parallel.Invoke` registry test asserting the type cache is built exactly once under concurrent
  first-use (finding H3).
- A flood test sending many malformed JSON frames asserting the connection is closed/throttled
  (finding H2).
- Per-field `JsonRpcServerConfig` validation tests (finding M5).
