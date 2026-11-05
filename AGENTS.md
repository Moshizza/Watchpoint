# Instructions for AI coding agents

All AI agents (Claude Code, Jules, Copilot, Codex, Gemini, …) follow the same rules:
**read [`CLAUDE.md`](CLAUDE.md) and follow it exactly.** It is the single source of truth;
this file only adds what differs for agents that are not Claude Code.

## Differences for non-Claude agents

- **Name**: use the name registered for you in the agent registry
  ([`Architecture/tasks.md`](Architecture/tasks.md#agent-registry)), e.g. `jules-1`. If you
  have none, stop and ask the person who started you.
- **Branch**: `<agent-name>/<task-id>-<slug>` as in CLAUDE.md §2. If your platform forces
  its own branch name, put the forced name in the task's **Owner** column instead and say so
  in the pull request description.
- **Issue comment**: if you cannot comment on the GitHub issue, write the claim in the pull
  request description (`Takes #<issue>`) instead.
- **Toolchain**: if the official .NET installer is reachable in your environment, use
  `https://dot.net/v1/dotnet-install.sh --channel 10.0` (no apt needed); otherwise follow
  CLAUDE.md §7.
- **Never merge.** Open the pull request, mark it ready for review, and stop. The person
  responsible merges after approval.
