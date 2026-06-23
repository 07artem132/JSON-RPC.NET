# Tasks — ci-bootstrap

- [x] 1 Add `.github/workflows/build.yml` (`CI`): push(main, claude/**) + PR + dispatch; ubuntu-latest.
- [x] 2 Vulnerability-audit gate: restore with audit ON + `dotnet list --vulnerable` grep → fail on hit.
- [x] 3 Build `-c Release` (warnings-as-errors via csproj) + test with TRX + coverage; upload artifact.
- [x] 4 SHA-pin actions (reuse `publish-nuget.yml` SHAs).
- [x] 5 README badge `dotnet-desktop.yml` → `build.yml`.
- [x] 6 Dry-run every step locally on net10 (restore/audit/build/test all green, artifacts produced).
- [x] 7 `AUDIT-FINDINGS.md` + `CLAUDE.md`: mark M8 (+ M7 badge) shipped; trim backlog.
