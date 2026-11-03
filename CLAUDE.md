# Rules for AI agents working on Watchpoint

These rules apply to every AI agent (Claude Code or other) working on this repository. They
are short on purpose: read them, then read only the spec sections your task names.

## 1. Before you start

1. **Name yourself.** Pick a unique agent name (`claude-<name>`, lowercase, e.g.
   `claude-atlas`) and add it to the **agent registry** in
   [`Architecture/tasks.md`](Architecture/tasks.md#agent-registry) with the person responsible
   for you. Reuse your name in later sessions; never use another agent's name.
2. **Pick a task** whose status is `todo` and whose dependencies are all `done`
   ([`Architecture/tasks.md`](Architecture/tasks.md)). One task per session.
3. **Read only what the task needs**: its row in `tasks.md`, the spec sections it names,
   [`Architecture/README.md`](Architecture/README.md) (conventions), and the issue. Do not
   re-read the whole architecture.

## 2. Claim before coding

Create your branch `<agent-name>/<task-id>-<slug>` from `main` (never work on `main`). In your
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
