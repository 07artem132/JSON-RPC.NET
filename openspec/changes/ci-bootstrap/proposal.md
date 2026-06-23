# ci-bootstrap — audit-enforcing CI workflow (M8 + M7 badge)

## Why

The repo had **no CI build/test workflow** — only `publish-nuget.yml` (runs on release). That's audit
finding **M8**: nothing automatically enforces the quality bar (0 warnings, green tests, no vulnerable
dependencies) on push/PR. The `security-hardening` work made that bar real — but a bar that only a human
remembers to run locally is the next regression. The recent MessagePack advisory is the concrete
motivator: a transitive vuln slipped in and was only caught because someone built with the audit on.

Separately, the README build-status badge (M7 leftover) pointed at `dotnet-desktop.yml`, which does not
exist in this repo (it's the SignalCli.NET workflow name) — so the badge was permanently broken.

## What Changes

- **New `.github/workflows/build.yml`** (`CI`), triggered on push to `main` / `claude/**`, on every
  pull request, and via `workflow_dispatch`. One `build-test` job on `ubuntu-latest`:
  1. **NuGet vulnerability audit gate** — `dotnet restore` with the audit **enabled** (no
     `-p:NuGetAudit=false`), then `dotnet list package --vulnerable --include-transitive` greped to fail
     the build if any vulnerable package (incl. transitive, incl. the lax `example/*` projects) is found.
  2. **Build** `JSON-RPC.NET.sln -c Release` — `TreatWarningsAsErrors` on lib + tests means any warning
     fails CI.
  3. **Test** the unit suite (90) with TRX logger + `XPlat Code Coverage`; upload results as an artifact.
- **README badge** repointed `dotnet-desktop.yml` → `build.yml` (closes the broken-badge part of M7).
- GitHub Actions are **SHA-pinned**, reusing the exact SHAs already vetted in `publish-nuget.yml`
  (`checkout` v5.0.1, `setup-dotnet` v5.2.0, `upload-artifact` v5.0.0) per `.claude/rules/csproj-build.md`.

**Out of scope:** OS matrix (windows/macos), coverage-badge auto-commit, and CI for the consumer app
(`SignalCliNet.WsRpcServer`, which needs private-feed auth). These are noted as follow-ups.

## Capabilities

### `ci-bootstrap`
A push to `main`/`claude/**` or any PR SHALL run a CI job that (a) fails on any vulnerable NuGet package,
(b) fails on any build warning (`TreatWarningsAsErrors`), and (c) runs the full unit suite. The README
build badge SHALL reference the workflow that actually exists in this repo.

## Verification

- YAML validates; every CI command dry-run locally on net10: restore clean, audit gate passes (4/4
  projects no vulnerable packages), build 0 warnings, 90/90 tests pass, TRX + cobertura artifacts emitted.
- No package version bump (CI is not a shippable artifact).
