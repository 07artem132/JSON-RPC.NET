# foundation-cluster-1 — build hygiene + README accuracy (→ 0.2.0)

## Why

`AUDIT-FINDINGS.md` (commit `2553920`) виявив 20 findings (4 HIGH + 9 MEDIUM + 7 LOW). Перш ніж адресувати HIGH-severity production risks (composition root, dispose patterns, thread safety), потрібен **чистий foundation**: build clean, README accurate, MSBuild shared canon. Без цього кроку будь-яке наступне виправлення буде ship'итися у tree з 108 warnings + broken badges + duplicate-config registration — нова робота не diff'ується від існуючого шуму.

Цей change закриває 4 LOW/MEDIUM findings ordered low-risk first, кожен як окремий capability у власному commit'і per CLAUDE.md (майбутній) `audit-debt.md § Working style` "One commit per capability/cluster". Усі 4 capabilities — non-breaking; жодного API surface change; жодного behavioral change.

Цільова версія: **0.2.0** (поточна — implicit 0.1.0; нічого з 1.x немає у NuGet feed'і).

## What Changes

| # | Capability | Findings | Files | Risk |
|---|---|---|---|---|
| 1 | `readme-org-fix` | M7 | `README.md` | none — text-only |
| 2 | `directory-build-props` | (M6 prep) | new `Directory.Build.props` | none |
| 3 | `warnings-cleanup` | M6 | `tests/WsRpcServer.Tests/**/*.cs` (~108 fixes) | low — test-only |
| 4 | `treat-warnings-errors` | M6 (finalize) | `src/WsRpcServer/WsRpcServer.csproj` + `tests/.../WsRpcServer.Tests.csproj` | none post-#3 |

### Why this ordering

- **#1 first** — text-only fix, no build impact, gives immediate consumer value (badges + NuGet feed correctness).
- **#2 second** — shared MSBuild prop file що використовується усіма наступними capabilities; standalone, no warnings yet.
- **#3 third** — silent-style fix of 108 existing warnings. Tests-only (no `src/**` modifications) тому baseline ≥ 83 passing tests preserved.
- **#4 last** — `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` activation; can ONLY land post-#3, інакше build breaks.

### Capability deltas

- **`readme-org-fix`** modifies:
  - Build status badge: `mil-development` → `07artem132` (line 7).
  - NuGet feed instructions: `mil-development/index.json` → `07artem132/index.json` (line 60).
  - Coverage badges (lines/methods/branches): paths залишаються `.github/badges/*.svg` — їхня генерація — out of scope (потребує CI, окремий future change).

- **`directory-build-props`** creates `Directory.Build.props` у repo root з:
  - `<AnalysisLevel>latest-recommended</AnalysisLevel>`
  - `<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>`
  - `<Nullable>enable</Nullable>` (lifts per-csproj duplicate; кожен csproj zараз має це окремо)
  - `<TreatWarningsAsErrors>false</TreatWarningsAsErrors>` (default; per-csproj override у `treat-warnings-errors`)
  - `<WsRpcServerPackageVersion>0.2.0</WsRpcServerPackageVersion>` — single source of truth, eliminate per-csproj `<Version>` hardcoding (як SignalCli.NET pattern).

- **`warnings-cleanup`** fixes:
  - ~90 nullability mismatches у `tests/WsRpcServer.Tests/Transport/WebSocketMessageHandlerTests.cs` + `tests/.../Sessions/TestJsonRpcSession.cs` (CS8602/CS8605/CS8620 у Moq logger formatters).
  - 2 xUnit1031 violations у `tests/.../Core/AbstractSubscriptionStoreTests.cs:515,556` (`Task.WaitAll(tasks)` → `await Task.WhenAll(tasks)` + make `[Fact]` method async).
  - 1 CS0168 unused `ex` у `WebSocketMessageHandlerTests.cs:455`.
  - 15+ minor CS86xx у remaining test files.

- **`treat-warnings-errors`** adds `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` to:
  - `src/WsRpcServer/WsRpcServer.csproj`
  - `tests/WsRpcServer.Tests/WsRpcServer.Tests.csproj`
  - (NOT to `example/SimpleServer/SimpleServer.csproj` чи `example/SimpleClient/` — example projects intentionally lax; consumer code, not framework code.)

## Capabilities

### New Capabilities

- **`readme-org-fix`**: README.md SHALL reference the correct GitHub organization (`07artem132`) у build-status badge URL і NuGet feed URL. Existing references to `mil-development` SHALL be replaced verbatim. Coverage badge paths залишаються нерухомими (`.github/badges/*.svg`).

- **`directory-build-props`**: `Directory.Build.props` SHALL exist at repo root з shared MSBuild properties: `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild=true`, `Nullable=enable`, `TreatWarningsAsErrors=false` (default), `WsRpcServerPackageVersion` як single-source-of-truth для main package version.

- **`warnings-cleanup`**: `dotnet build` SHALL produce 0 warnings across `src/WsRpcServer.csproj` + `tests/WsRpcServer.Tests.csproj`. CS86xx nullability mismatches у тестах SHALL be fixed via correct Moq formatters / typed locals / null-forgiving operator де нульабельність дійсно guaranteed. xUnit1031 blocking-in-test SHALL be replaced via `await Task.WhenAll(...)`. CS0168 unused variables SHALL be removed.

- **`treat-warnings-errors`**: `src/WsRpcServer.csproj` AND `tests/WsRpcServer.Tests.csproj` SHALL declare `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`. Build SHALL fail if any analyzer warning surfaces. Example projects (`example/SimpleServer`, `example/SimpleClient`) SHALL NOT carry this flag — consumer code with educational intent залишається lax.

### Modified Capabilities

- **None.** Це первинна capability-таблиця для repo (codebase shipped без OpenSpec process).

## Out of scope

- **Source-side warnings** — у baseline build CS86xx у `src/` = 0. Усі 108 warnings — у tests/. Future capabilities (composition-root, dispose-async) можуть наклеїти source-side warnings; вони адресуються тоді, не зараз.
- **AOT readiness review** — separate future change `aot-readiness-review` (H3 + IL2026/IL3050 audit). Reflection-heavy code (`AbstractRpcServiceRegistry`) залишається untouched.
- **CI workflows** — окремий `ci-bootstrap` change. Без CI badges у README залишаються "broken images" — acceptable у foundation cluster.
- **Composition root completeness** (H1) — окремий `composition-root-complete` change. Це HIGH severity; пишемо окрему OpenSpec proposal з design.md що описує generic-параметризовану signature.
- **Dispose async pattern** (H4), **JSON parse throttling** (H2), **Service registry thread-safety** (H3) — окремі HIGH-severity changes.
- **CLAUDE.md / `.claude/rules/` scaffold** — окремий low-priority `claude-md-scaffold` change.
- **WsRpcServerPackageVersion bump до 0.2.0** — landed у capability `directory-build-props`. Consumer-facing artifact: NuGet package version bumps.
