---
paths:
  - "**/*.csproj"
  - "Directory.Build.props"
  - ".github/workflows/**"
---

# csproj / MSBuild + CI conventions

- **`Directory.Build.props` is shared canon** — auto-imported by every csproj. It holds
  `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild=true`, `Nullable=enable`,
  `ImplicitUsings=enable`, and `<WsRpcServerPackageVersion>` — the **single source of truth** for the
  package version. The repo-wide `NoWarn=CA1848;CA1873` is **gone** — `logger-message-migration` (2.1.0)
  moved all `src/WsRpcServer` logging onto source-generated `[LoggerMessage]` partials
  (`src/WsRpcServer/Logging/*Log.cs`), so both perf rules are now active there. They stay suppressed only
  in the **test** csproj (test doubles log ad-hoc) and the **example** projects
  (`example/Directory.Build.props`, idiomatic consumer demos) — the same test-only/demo-only carve-out as
  `CA1707`. Note: `example/Directory.Build.props` must explicitly `<Import>` the root props (MSBuild only
  auto-imports the *nearest* `Directory.Build.props`).
- **Version goes through `$(WsRpcServerPackageVersion)`, never hardcoded.**
  `WsRpcServer.csproj` uses `<Version>$(WsRpcServerPackageVersion)</Version>` +
  `<AssemblyVersion>` + `<FileVersion>`. A hardcoded `<Version>X.Y.Z</Version>` is the exact drift
  finding from `foundation-cluster-1`; don't reintroduce it. Bumping the package = one edit in
  `Directory.Build.props`.
- **`TreatWarningsAsErrors=true` is per-csproj, not in the props.** The props default is `false`;
  `src/WsRpcServer/WsRpcServer.csproj` + `tests/WsRpcServer.Tests/WsRpcServer.Tests.csproj` opt in.
  The `example/*` projects stay opt-out (educational consumer code). Don't move the flag into the props
  — that would break the example builds.
- **Narrow `NoWarn` only.** `CS1591` (missing XML doc) is suppressed on the lib because doc-gen is on;
  `CA1707` (underscores in identifiers) is suppressed on the **test** csproj only (xUnit naming). Don't
  add blanket suppressions to hide a real warning — fix the warning or suppress at the call site with a
  justification.
- **GitHub Actions: SHA-pin every `uses:` and copy SHAs from existing workflows in this repo**
  (`.github/workflows/publish-nuget.yml`), not from memory or docs. A 1-char typo in an action SHA
  fast-fails with "Unable to resolve action". The publish workflow installs the **net10 SDK** and the
  lib/tests target **net10.0**; keep the package version flowing from `Directory.Build.props`.
- CI lives in `.github/workflows/build.yml` (`CI`, the `ci-bootstrap` change): a NuGet
  vulnerability-audit gate + warnings-as-errors build + the unit suite on push/PR. `publish-nuget.yml`
  still handles release-time packaging. Keep both workflows' `actions/*` **SHA-pinned** from each other
  (don't introduce a floating `@v4` tag). When extending CI, prefer fast static checks over heavyweight
  consumer-build simulations.
