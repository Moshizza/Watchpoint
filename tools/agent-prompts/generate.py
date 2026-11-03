#!/usr/bin/env python3
"""Generate one ready-to-paste session prompt per task, in dependency order.

Source of truth: Architecture/tasks.md (task rows) and Architecture/build-order.md (spec files).
Output: Architecture/agent-prompts.md. Re-run after editing tasks.md:

    python3 tools/agent-prompts/generate.py [--agent claude-atlas] [--person "Mohcine (Moshizza)"]
"""
from __future__ import annotations

import argparse
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TASKS = ROOT / "Architecture" / "tasks.md"
BUILD_ORDER = ROOT / "Architecture" / "build-order.md"
OUT = ROOT / "Architecture" / "agent-prompts.md"

TEST_DOCS = {
    "tests/e2e-scenarios": "Architecture/tests/e2e-scenarios.md",
    "tests/performance-tests": "Architecture/tests/performance-tests.md",
    "tests/security-tests": "Architecture/tests/security-tests.md",
}
UI_SPECS = {"UIC", "UIO", "UIF", "DSK", "WEB", "MOB"}
STOP_WORDS = {"and", "the", "of", "with", "for", "a", "an", "to", "per", "by", "in", "on"}


def task_rows() -> list[dict]:
    rows = []
    for line in TASKS.read_text(encoding="utf-8").splitlines():
        if not re.match(r"\| T\d+\.\d\d \|", line):
            continue
        c = [x.strip() for x in line.split("|")]
        issue = re.search(r"#(\d+)", c[6])
        rows.append({"id": c[1], "title": c[2], "spec": c[3], "deps": c[4], "tests": c[5],
                     "issue": issue.group(1) if issue else None, "status": c[8]})
    return rows


def spec_files() -> dict[str, str]:
    files = {}
    for line in BUILD_ORDER.read_text(encoding="utf-8").splitlines():
        m = re.match(r"\| ([A-Z][A-Z-]*) \|[^|]*\| \[(specs/[^\]]+)\]", line)
        if m:
            files[m.group(1)] = "Architecture/" + m.group(2)
    return files


def dep_ids(deps: str, all_ids: list[str]) -> list[str]:
    out: list[str] = []
    for a, b in re.findall(r"(T\d+\.\d\d)(?:…(T\d+\.\d\d))?", deps):
        if b:
            ia, ib = all_ids.index(a), all_ids.index(b)
            out += all_ids[min(ia, ib): max(ia, ib) + 1]
        else:
            out.append(a)
    return [d for d in out if d in all_ids]


def order(rows: list[dict]) -> list[dict]:
    ids = [r["id"] for r in rows]
    deps = {r["id"]: dep_ids(r["deps"], ids) for r in rows}
    done, result = set(), []
    while len(result) < len(rows):
        progressed = False
        for r in rows:  # file order as tie-break
            if r["id"] not in done and all(d in done for d in deps[r["id"]]):
                result.append(r)
                done.add(r["id"])
                progressed = True
                break
        if not progressed:
            raise SystemExit("Dependency cycle in tasks.md")
    return result


def slug(title: str) -> str:
    text = re.sub(r"`[^`]*`|\([^)]*\)", " ", title.split(":")[-1] if ":" in title else title)
    words = [w for w in re.findall(r"[a-z0-9]+", text.lower()) if w not in STOP_WORDS]
    return "-".join(words[:4]) or "task"


def prompt(r: dict, files: dict[str, str], agent: str, person: str) -> str:
    spec_id = r["spec"].split()[0]
    spec_file = TEST_DOCS.get(spec_id) or files.get(spec_id, "(see build-order.md)")
    branch = f"{agent}/{r['id']}-{slug(r['title'])}"
    issue = f"GitHub issue #{r['issue']}" if r["issue"] else "no GitHub issue yet (use the fallback claim in tasks.md §1.2)"
    closes = f'with "Closes #{r["issue"]}"' if r["issue"] else "(no issue to close)"
    ui = spec_id in UI_SPECS or "UI" in r["title"] or "editor" in r["title"].lower()
    spike = r["title"].lower().startswith("spike")
    deps = r["deps"] if r["deps"] != "—" else "none"
    extra = []
    if ui:
        extra.append("- UI work: install Node 24 LTS from nodejs.org (download the linux-x64 tarball and verify it against "
                     "SHASUMS256.txt), then use `npx -y pnpm@<version in package.json packageManager>`; never use the "
                     "container's Node 22 (the workspace refuses it).")
    if spike:
        extra.append("- Spike: the deliverable is a decision record `Architecture/decisions/ADR-<nnn>-<topic>.md` "
                     "(context, options, measurements, decision, consequences) plus any throwaway code under "
                     "`tools/spikes/` that the record references; update the affected spec sections.")
    if spec_id.startswith("tests/"):
        extra.append("- Test task: implement the listed scenarios/tests exactly as written in the test document; "
                     "missing product behaviour is reported, not patched around.")
    extra_text = ("\n" + "\n".join(extra)) if extra else ""
    return f"""You are `{agent}`, an AI agent registered in Architecture/tasks.md (agent registry), working for {person} on the repository Moshizza/Watchpoint.

TASK: take {r['id']} "{r['title']}" ({issue}). Spec: {r['spec']} — {spec_file}. Depends on: {deps}. Tests (all must exist, carry their ids, and pass): {r['tests']}.

RULES (follow CLAUDE.md at the repo root exactly; key points):
1. First check in Architecture/tasks.md on origin/main that every dependency above is `done` and {r['id']} is `todo`. If not, STOP and report.
2. Read ONLY: CLAUDE.md, the {r['id']} row in Architecture/tasks.md, the spec sections named above (and that spec's tests table rows for the listed ids), and Architecture/README.md §3, §5, §7 (UI only), §9, §11, §12. Read other specs only for a contract you must call, and only that section. Do not read docs/.
3. Branch: `{branch}` from `origin/main`. Use this branch name even if the session suggests another branch. Never commit to main.
4. First commit on the branch: in Architecture/tasks.md set {r['id']} Owner = `{branch}`, Status = `in progress`. Push, open a DRAFT pull request titled "{r['id']} <short title>" {closes}, and comment on the issue that {agent} took it (with the branch name).
5. Push after EVERY logical step (one type or feature + its tests). Commit titles `feat({r['id']}): …`, `test({r['id']}): …`, `fix({r['id']}): …`, `wip({r['id']}): …` for unfinished work. Commit bodies end with:
   Agent: {agent}
   Update the PR checklist only 3 times: draft, half way, ready for review.
6. If the session ends early nothing must be lost: everything is pushed. A later session resumes with "Resume {r['id']}".
7. When done: all listed tests pass locally (integration tests may run in CI only if Docker is unavailable) and in CI (`ci-ok` green on the PR), no warnings. Set Status = `in review`, mark the PR ready for review, then STOP and wait. NEVER merge without {person.split(' ')[0]} writing "I approve". After approval: set Status = `done` in the same PR, wait for CI, squash-merge.
8. If the spec is wrong or incomplete, fix the spec in the same PR and say so in the PR description.

TECHNICAL NOTES (already decided, do not re-investigate):
- The official .NET download site is blocked here. Install the SDK with: `apt-get update && apt-get install -y dotnet-sdk-10.0` (10.0.1xx, matching global.json).
- Tests use Microsoft.Testing.Platform (global.json "test.runner"). Test projects: `<OutputType>Exe</OutputType>`, reference `xunit.v3` and the libraries the spec names; no Microsoft.NET.Test.Sdk or xunit.runner.visualstudio. Run: `dotnet test --solution src/service/Watchpoint.slnx`. Follow the patterns already used in tests/service/Watchpoint.Kernel.Tests.
- Package versions go in the root Directory.Packages.props (central management, exact versions, latest stable from api.nuget.org / npm). Commit generated lock files (packages.lock.json, pnpm-lock.yaml); CI restores in locked mode.
- New projects go where Architecture/README.md §3 says and are added to src/service/Watchpoint.slnx. Contract projects (Kernel, *.Abstractions) need XML docs on every public member.
- Build settings (warnings as errors, code style enforced) come from the root Directory.Build.props and .editorconfig: run `dotnet build src/service/Watchpoint.slnx -c Release` before every push.
- The container may have no Docker daemon: Testcontainers-based tests then run only in CI (GitHub runners have Docker).
- CI: check the PR's runs with the GitHub tools; use `sleep` only in background commands.{extra_text}

COST: keep replies short, no long explanations, no re-reading files you already read, no summaries of the spec. Report to me only: PR link when opened, problems that need my decision, and the final result."""


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--agent", default="claude-atlas")
    ap.add_argument("--person", default="Mohcine (Moshizza)")
    a = ap.parse_args()
    rows = order(task_rows())
    files = spec_files()
    todo = [r for r in rows if r["status"] not in ("done",)]
    lines = [
        "# Agent session prompts (generated)",
        "",
        "One ready-to-paste prompt per task, **in dependency order**: start a new chat (with the",
        "repository Moshizza/Watchpoint selected), paste the prompt of the first task that is still",
        "`todo` in [tasks.md](tasks.md), and wait for it to finish before the next one (unless both",
        "only depend on `done` tasks — then they can run in parallel in separate chats).",
        "",
        "Generated by `python3 tools/agent-prompts/generate.py` from tasks.md; do not edit by hand.",
        "To resume a task cut short, start a new chat with: `You are <agent>. Resume <task-id>.`",
        "",
        "## Order",
        "",
        "| # | Task | Status | Depends on |",
        "|---|---|---|---|",
    ]
    for i, r in enumerate(todo, 1):
        lines.append(f"| {i} | [{r['id']}](#{r['id'].lower().replace('.', '')}) {r['title']} | {r['status']} | {r['deps']} |")
    lines += ["", "## Prompts", ""]
    for r in todo:
        lines += [f"### {r['id']}", ""]
        if r["status"].startswith("blocked"):
            lines += [f"Blocked ({r['status']}): no prompt until the spec is written.", ""]
            continue
        lines += ["```text", prompt(r, files, a.agent, a.person), "```", ""]
    OUT.write_text("\n".join(lines), encoding="utf-8")
    print(f"{len(todo)} tasks written to {OUT.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
