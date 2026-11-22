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
- **Claim when you cannot push early** (e.g. Jules): your branch is invisible until you
  submit, so the person starting you comments `Taken by <agent-name>` on the issue *before*
  you start, and gives you the task id explicitly. Other agents skip tasks with that comment
  (CLAUDE.md §1.2). If you cannot comment on the issue yourself, write the claim in the pull
  request description (`Takes #<issue>`).
- **Toolchain**: if the official .NET installer is reachable in your environment, use
  `https://dot.net/v1/dotnet-install.sh --channel 10.0.1xx` (the band pinned by `global.json`; plain `--channel 10.0` installs a newer band that `global.json` refuses); otherwise follow
  CLAUDE.md §7.
- **Never merge.** Open the pull request, mark it ready for review, and stop. The person
  responsible merges after approval.
- **Coordination file**: keep your status file on the `coordination` branch
  ([`Architecture/coordination.md`](Architecture/coordination.md)). If your platform cannot push
  before you submit, ask the person who started you to create and update it for you.
