# Rules for AI agents working on Watchpoint

These rules apply to every AI agent (Claude Code or other) working on this repository. They
are short on purpose: read them, then read only the spec sections your task names.

## 1. Before you start

1. **Name yourself.** Pick a unique agent name (`claude-<name>`, lowercase, e.g.
   `claude-atlas`) and add it to the **agent registry** in
   [`Architecture/tasks.md`](Architecture/tasks.md#agent-registry) with the person responsible
   for you. Reuse your name in later sessions; never use another agent's name.
2. **Pick a task.** If you were given a task id, take it; otherwise take the **next available
   task**: `git fetch origin` then `python3 tools/next-task.py` (first task in dependency order
   that is `todo`, has all dependencies `done`, and has no remote branch `*/<task-id>-*` yet).
   The script only sees remote branches, so also skip a task whose issue has a
   `Taken by <agent>` comment or that has an open pull request (some agents, e.g. Jules,
   cannot push before they submit; their claim is that issue comment). If it prints that no
   task is available, report that and stop. One task per session.
3. **Read only what the task needs**: its row in `tasks.md`, the spec sections it names,
   [`Architecture/README.md`](Architecture/README.md) (conventions), and the issue. Do not
   re-read the whole architecture.

## 2. Claim before coding

Run `git fetch origin` and check again that no branch `*/<task-id>-*` exists, that the issue
has no `Taken by <agent>` comment and that no open pull request closes it (someone may have
started meanwhile; if so, pick the next task). Create your branch `<agent-name>/<task-id>-<slug>`
from `origin/main` (never work on `main`); the slug is 2–5 lowercase words from the task title. In your
first commit on that branch, set the task's **Owner** to that exact branch name, for example
`claude-atlas/T0.01-repository-skeleton`, and **Status** to `in progress`. Push it, open a
**draft** pull request titled `<task-id> <title>` with `Closes #<issue>`, and comment on the
issue that you took it (with the branch name).

People working without an agent follow the same pattern with their GitHub username, e.g.
`moshizza/T1.04-storage`.

## 3. While working (nothing may be lost)

- **Push after every logical step** (one file group, one class with its tests…). Commit titles:
  `feat(<task-id>): …`, `test(<task-id>): …`, `fix(<task-id>): …`, `docs(<task-id>): …`, or
  `wip(<task-id>): …` for unfinished steps. Work that is not pushed is lost when the session
  ends.
- **Update the pull request checklist only three times**: when opening the draft, at about
  half way, and when ready for review. Commit titles are the detailed progress record.
- No separate progress files.
- If a spec is wrong or incomplete, fix the spec in the same pull request and say so in the
  description (label `docs-mismatch` on the issue).

## 4. Resuming

To continue a task after a cut ("resume <task-id>"): find the branch `*/<task-id>-*`, read its
pull request checklist and `git log`, then continue from the last pushed commit.

## 5. Finishing

1. Meet the [definition of done](Architecture/README.md#12-definition-of-done-every-task):
   all listed tests exist and pass, build without warnings, formatting clean.
2. Set the task's **Status** to `in review`, tick the checklist, mark the pull request
   **ready for review**.
3. **Never merge without the approval of the person responsible for you**, and never push to
   `main` directly. After approval: squash merge, set Status to `done` in the same pull
   request, delete the branch.

## 6. Commit and pull request footer

Add a line `Agent: <agent-name>` to commit message bodies and pull request descriptions.

## 7. Technical notes (already decided, do not re-investigate)

- **.NET SDK**: the official download site is blocked in Claude cloud sessions. Install with
  `apt-get update && apt-get install -y dotnet-sdk-10.0` (10.0.1xx, matches `global.json`).
- **Node 24** (UI tasks only): the container's Node 22 is refused by the workspace. Download the
  linux-x64 tarball from nodejs.org, verify it against `SHASUMS256.txt`, put it first in `PATH`,
  and use `npx -y pnpm@<version of packageManager in package.json>`.
- **Tests** use Microsoft.Testing.Platform (`global.json` `test.runner`). Test projects:
  `<OutputType>Exe</OutputType>`, reference `xunit.v3` plus the libraries the spec names; no
  `Microsoft.NET.Test.Sdk` or `xunit.runner.visualstudio`; add `using Xunit;`. Run
  `dotnet test --solution src/service/Watchpoint.slnx`. Follow existing test projects.
- **Packages**: versions only in the root `Directory.Packages.props` (exact, latest stable);
  commit lock files (`packages.lock.json`, `pnpm-lock.yaml`) — CI restores in locked mode.
- **Projects** go where [Architecture/README.md §3](Architecture/README.md#3-repository-layout)
  says and are added to `src/service/Watchpoint.slnx`. Contract projects (`Watchpoint.Kernel`,
  `*.Abstractions`) need XML docs on every public member.
- **Before every push**: `dotnet build src/service/Watchpoint.slnx -c Release` (warnings are
  errors, code style enforced) and the tests you touched.
- **Docker** may be unavailable in the session: Testcontainers-based tests then run only in CI.
- **CI**: check the pull request's runs with the GitHub tools; `sleep` only in background
  commands.
- **Spikes** deliver a decision record `Architecture/decisions/ADR-<nnn>-<topic>.md` (context,
  options, measurements, decision, consequences) and update the affected spec sections.
- **Cost**: keep replies short; do not re-read files; report only the PR link, decisions you
  need, and the final result.

## 8. Session prompt (for the person starting a session)

```text
You are `<agent-name>`, an AI agent working for <person> on Moshizza/Watchpoint.
Take the next available task: run `python3 tools/next-task.py` on an up-to-date origin/main and
follow CLAUDE.md exactly (claim, push after every step, draft PR, stop at ready for review,
never merge without my "I approve"). If no task is available, say so and stop.
Keep replies short; report only the PR link, decisions you need from me, and the final result.
```

To continue an interrupted task: `You are <agent-name>. Resume <task-id>.`

