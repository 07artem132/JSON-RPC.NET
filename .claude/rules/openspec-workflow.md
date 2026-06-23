---
paths:
  - "openspec/**"
  - "CLAUDE.md"
  - "AUDIT-FINDINGS.md"
---

# OpenSpec workflow

Non-trivial work in this repo goes through **OpenSpec** (the `/opsx:*` commands + skills are installed
under `.claude/`). The shape, mirrored from SignalCli.NET:

- **Plan first.** A change lives under `openspec/changes/<change-name>/` with `proposal.md` (what & why),
  optionally `design.md` (how), `tasks.md` (steps), and one `specs/<capability>/spec.md` per capability.
  Validate with `openspec validate --strict` when the CLI is available.
- **`AUDIT-FINDINGS.md` is the backlog.** It enumerates 20 findings (4 HIGH / 9 MEDIUM / 7 LOW) with a
  proposed capability-shaped roadmap at the bottom. Pull the next capability from there; don't invent a
  parallel backlog. When a finding is shipped, note it (and its guard test) so the audit doc and CLAUDE.md
  don't drift from the code.
- **One commit per capability/cluster.** Each capability lands as its own commit with a clear message;
  a multi-capability cluster (like `foundation-cluster-1`) is reviewed/bisected capability-by-capability.
  Final batch (version bump, docs) in one trailing commit.
- **`dotnet build` + `dotnet test` after every capability.** If the test count drops or a warning
  appears, stop and diagnose before continuing.
- **File the change *before* fixing — even for HIGH findings.** The regression-guard test that prevents
  recurrence is the durable artifact, not the one-off fix. A fix without a guard is just one file showing
  the right text today.

## Versioning

- The package version lives only in `Directory.Build.props` (`<WsRpcServerPackageVersion>`); a version
  bump is a deliberate part of a change's trailing commit. Non-breaking foundation/feature work → minor
  bump; breaking API change → major. There is no CHANGELOG.md in this repo yet — if you add one, keep it
  in lockstep with the version bump (same commit), the SignalCli.NET convention.
