---
paths:
  - "**/*.csproj"
  - "Directory.Build.props"
  - ".github/workflows/**"
---

# csproj / MSBuild + CI conventions

- **`Directory.Build.props` is shared canon** — auto-imported by every csproj. It holds
  `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild=true`, `Nullable=enable`,
  `ImplicitUsings=enable`, the deferred `NoWarn=CA1848;CA1873` (LoggerMessage migration is a separate
  future capability), and `<WsRpcServerPackageVersion>` — the **single source of truth** for the
  package version.
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
  fast-fails with "Unable to resolve action". The publish workflow installs the **net10 SDK** to build
  the net9.0 target — that's fine (newer SDK builds older TFMs); keep the package version flowing from
  `Directory.Build.props`.
- There is **no CI build/test workflow yet** (only `publish-nuget.yml` on release) — that's open audit
  finding **M8** (`ci-bootstrap`). When adding it, prefer fast static checks over heavyweight
  consumer-build simulations.
