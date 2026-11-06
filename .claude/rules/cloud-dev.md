---
paths:
  - ".claude/**"
---

# Cloud development (Claude Code on the web)

A `SessionStart` hook (`.claude/hooks/session-start.sh`, wired via `.claude/settings.json`) prepares
remote sessions: it installs the SDK pinned in `global.json` via `dotnet-install.sh` (the distro `dotnet-sdk-10.0` apt package is 10.0.1xx and doesn't satisfy it), warms the NuGet cache by
restoring `tests/WsRpcServer.Tests`, and does a sanity build of `src/WsRpcServer`. It runs **only** when
`CLAUDE_CODE_REMOTE=true`, so local workflows are untouched, and it's idempotent.

Unlike SignalCli.NET, this repo has **no private NuGet feed** — all dependencies resolve from nuget.org,
so the restore needs no token and no `--source` override.

For background on environments, network policy and triggers, see
https://code.claude.com/docs/en/claude-code-on-the-web.
