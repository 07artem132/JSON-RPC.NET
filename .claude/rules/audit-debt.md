# Audit debt + working style + prevention checklist

Cross-cutting agent-instruction rules that apply to ANY edit session in this repo. Loaded always
(no `paths:` frontmatter) because every change benefits from these. The authoritative finding list
is `AUDIT-FINDINGS.md` (commit-cited static audit, 2026-05-27).

## Open audit findings (the real backlog)

`AUDIT-FINDINGS.md` enumerates **20 findings**. As of `foundation-cluster-1` (1.1.0) the build-hygiene
findings (M6 warnings, M7 README org refs) are **shipped**. Still open:

| Sev | ID | One-liner | Suggested capability |
|---|---|---|---|
| ✅ | H1 | `AddJsonRpcCore` registers only config; consumer hand-wires 5 services; no idempotency guard → shipped `composition-and-config` (1.3.0) | `composition-root-complete` |
| ✅ | H2 | Unbounded malformed-JSON recovery loop = single-connection CPU-burn DoS → shipped `security-hardening` (1.2.0) | `parse-failure-throttle` |
| ✅ | H3 | Reflection registry thread-unsafe lazy cache + silent multi-impl loss → shipped `security-hardening` (1.2.0); AOT still open | `service-registry-thread-safety` |
| ✅ | H4 | `Dispose()` doesn't cancel CTS first → shipped `security-hardening` (1.2.0) | `dispose-cancellation` |
| ✅ | M2/M3/M4 | Subscription base: unused lock; `account` leaks domain; `object` event types lose type-safety → shipped `subscription-manager-cleanup` (2.0.0, BREAKING) | `subscription-manager-cleanup` |
| ✅ | M5 | `JsonRpcServerConfig` has no `[Range]`/`[Required]` validation → shipped `composition-and-config` (1.3.0) | `config-validation` |
| ✅ | M8 | No CI build/test workflow → shipped: `.github/workflows/build.yml` (audit gate + tests) | `ci-bootstrap` |
| ✅ | M9 | `WebSocketMessageHandler.CanRead/CanWrite` hardcoded `true` → shipped `security-hardening` (1.2.0) | (folded into H2) |
| ✅ | M1 | Fire-and-forget notification failures lost; broken client never removed → shipped `low-severity-polish` (2.2.0) | `event-processor-resilience` |
| ✅ | L1-L7 | overwrite-without-warning, non-concurrent list, `new` vs `override`, misleading async sig, sealed exception → shipped `low-severity-polish` (2.2.0); L5/L7 already resolved | polish |

**Only the registry AOT source-gen discovery alternative remains open** (rule #4 / H3 follow-up): the
reflection scan in `AbstractRpcServiceRegistry` is not AOT-compatible (IL2026/IL3050). Closing it needs a
source-generator-based discovery path (and likely a small public-API addition) before
`<IsAotCompatible>true</IsAotCompatible>` can be set. Everything else in `AUDIT-FINDINGS.md` is shipped.

**Rule for new PRs:** if your change touches the code behind an open finding, fix the finding (and add
its guard test) in the same change rather than building new code on top of the debt.

## Prevention checklist (how the existing issues got in)

- **Version drift.** If you change a version anywhere, confirm it flows through
  `Directory.Build.props → $(WsRpcServerPackageVersion)` and is not hardcoded in a csproj.
- **Silent test-project warnings.** `dotnet build` must be 0 warnings in **both** `src/WsRpcServer` and
  `tests/WsRpcServer.Tests`. A new test must not introduce an analyzer warning (especially `xUnit1031`).
- **Doc/code drift.** If you change a named constant/threshold or rename a public type, `grep` `README.md`,
  `CLAUDE.md` and `AUDIT-FINDINGS.md` for the old name/value and update them in the same change.
- **Fix without a guard.** A declared invariant with no test is the next regression. Prefer adding a small
  reflection/behavioral guard (see `.claude/rules/testing.md`) over trusting prose.

## Working style

- **Plan first, then implement** — OpenSpec proposal before non-trivial work (see `openspec-workflow.md`).
- **One commit per capability/cluster.**
- **`dotnet build` + `dotnet test` after every cluster**; treat a test-count drop as an early warning.
- **Don't claim a failing test is "pre-existing" without a baseline check** — `git stash`, rebuild/retest
  at HEAD, compare.
- **Subagents for parallel *research*, not for write tasks** — "find all callsites of X", not "implement
  cluster Y for me".
- **Comments and log messages stay in Ukrainian.** Commit/PR titles may be Ukrainian or English — mirror
  the surrounding style.
- **Don't create new `*.md` docs unless asked.** `README.md`, `CLAUDE.md`, `AUDIT-FINDINGS.md` and OpenSpec
  change documents are the durable docs.
- **Use the Microsoft Learn MCP for any .NET/Microsoft API question before coding** — confirm package ids
  and overload signatures instead of guessing.
- **GitHub Copilot reads `.github/copilot-instructions.md`, not `CLAUDE.md`** — this repo ships a one-line
  pointer there so Copilot/Cursor users land on this guidance.
