#!/usr/bin/env python3
"""Print the next available task of Architecture/tasks.md (CLAUDE.md §1).

A task is available when its status is `todo`, all its dependencies are `done`, and nobody
has started it yet (no remote branch named `*/<task-id>-*` or `<task-id>-*`).
Tasks are considered in dependency order (file order as tie-break).

Usage (from the repository root, after `git fetch origin`):
    python3 tools/next-task.py            # next available task (exit 1 if none)
    python3 tools/next-task.py --list     # whole order with status and availability
    python3 tools/next-task.py --offline  # do not query remote branches
    python3 tools/next-task.py --self-test  # run the built-in checks
"""
from __future__ import annotations

import argparse
import re
import subprocess
import sys
from pathlib import Path

TID = r"T\d+\.\d\d[a-z]?"  # task id, optional letter suffix (T1.07c)
ROOT = Path(__file__).resolve().parents[1]
BUILD_ORDER = ROOT / "Architecture" / "build-order.md"


def read_tasks(text: str) -> list[dict]:
    rows = []
    for line in text.splitlines():
        if re.match(rf"\| {TID} \|", line):
            c = [x.strip() for x in line.split("|")]
            issue = re.search(r"#(\d+)", c[6])
            rows.append({"id": c[1], "title": c[2], "spec": c[3], "deps": c[4], "tests": c[5],
                         "issue": issue.group(1) if issue else None, "owner": c[7], "status": c[8]})
    return rows


def dep_ids(deps: str, ids: list[str]) -> list[str]:
    out: list[str] = []
    for a, b in re.findall(rf"({TID})(?:…({TID}))?", deps):
        if b and a in ids and b in ids:
            i, j = sorted((ids.index(a), ids.index(b)))
            out += ids[i: j + 1]
        else:
            out.append(a)
    return [d for d in out if d in ids]


def dependency_order(rows: list[dict]) -> list[dict]:
    ids = [r["id"] for r in rows]
    for r in rows:
        r["dep_ids"] = dep_ids(r["deps"], ids)
    placed: set[str] = set()
    result: list[dict] = []
    while len(result) < len(rows):
        nxt = next((r for r in rows if r["id"] not in placed and all(d in placed for d in r["dep_ids"])), None)
        if nxt is None:
            sys.exit("Dependency cycle in tasks.md")
        result.append(nxt)
        placed.add(nxt["id"])
    return result


def started_task_ids(offline: bool) -> set[str]:
    if offline:
        return set()
    try:
        out = subprocess.run(["git", "ls-remote", "--heads", "origin"], cwd=ROOT, check=True,
                             capture_output=True, text=True, timeout=60).stdout
    except (subprocess.SubprocessError, OSError) as e:
        sys.exit(f"Cannot list remote branches ({e}); retry or use --offline.")
    return set(re.findall(rf"refs/heads/(?:[^/\s]+/)?({TID})-", out))


def spec_file(spec: str) -> str:
    sid = spec.split()[0]
    if sid.startswith("tests/"):
        return f"Architecture/{sid}.md"
    for line in BUILD_ORDER.read_text(encoding="utf-8").splitlines():
        m = re.match(rf"\| {re.escape(sid)} \|[^|]*\| \[(specs/[^\]]+)\]", line)
        if m:
            return "Architecture/" + m.group(1)
    return "(see Architecture/build-order.md)"


def self_test() -> None:
    rows = read_tasks("\n".join([
        "| T1.07 | a | S | — | t | — | — | done |",
        "| T1.07c | b | S | T1.07 | t | — | — | todo |",
        "| T1.11 | c | S | T1.10, T1.07c | t | — | — | todo |",
        "| T1.10 | d | S | T1.07 | t | — | — | done |",
        "| T0.16a | e | S | — | t | — | — | done |",
        "| T1.12 | f | S | T1.07…T1.07c | t | — | — | todo |",
    ]))
    assert [r["id"] for r in rows] == ["T1.07", "T1.07c", "T1.11", "T1.10", "T0.16a", "T1.12"], rows
    ids = [r["id"] for r in rows]
    assert dep_ids("T1.10, T1.07c", ids) == ["T1.10", "T1.07c"]
    assert dep_ids("T1.07", ids) == ["T1.07"]
    assert dep_ids("T1.07…T1.07c", ids) == ["T1.07", "T1.07c"]
    order = [r["id"] for r in dependency_order(rows)]
    assert order.index("T1.07c") < order.index("T1.11"), order
    out = "refs/heads/x/T1.07c-y\nrefs/heads/T0.16a-z\nrefs/heads/jules-1/T1.07-q"
    assert set(re.findall(rf"refs/heads/(?:[^/\s]+/)?({TID})-", out)) == {"T1.07c", "T0.16a", "T1.07"}
    only = "refs/heads/x/T1.07c-y"
    assert set(re.findall(rf"refs/heads/(?:[^/\s]+/)?({TID})-", only)) == {"T1.07c"}
    print("self-test OK")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--self-test", action="store_true")
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--offline", action="store_true")
    ap.add_argument("--tasks", default=None, help="tasks.md path (default: origin/main version, else working copy)")
    a = ap.parse_args()
    if a.self_test:
        self_test()
        return

    if a.tasks:
        text = Path(a.tasks).read_text(encoding="utf-8")
    else:
        try:
            text = subprocess.run(["git", "show", "origin/main:Architecture/tasks.md"], cwd=ROOT, check=True,
                                  capture_output=True, text=True).stdout
        except subprocess.SubprocessError:
            text = (ROOT / "Architecture" / "tasks.md").read_text(encoding="utf-8")

    rows = dependency_order(read_tasks(text))
    status = {r["id"]: r["status"] for r in rows}
    started = started_task_ids(a.offline)

    def available(r: dict) -> bool:
        return (r["status"] == "todo" and r["id"] not in started
                and all(status.get(d) == "done" for d in r["dep_ids"]))

    if a.list:
        for i, r in enumerate(rows, 1):
            flag = "AVAILABLE" if available(r) else ("started" if r["id"] in started and r["status"] == "todo" else "")
            print(f"{i:3} {r['id']:8} {r['status']:24} {flag:9} {r['title']}")
        return

    nxt = next((r for r in rows if available(r)), None)
    if nxt is None:
        print("No task is available (all remaining tasks are started, blocked, or waiting for dependencies).")
        sys.exit(1)
    print(f"Next task: {nxt['id']} — {nxt['title']}")
    print(f"Spec:      {nxt['spec']} — {spec_file(nxt['spec'])}")
    print(f"Depends:   {nxt['deps']}")
    print(f"Tests:     {nxt['tests']}")
    print(f"Issue:     #{nxt['issue']}" if nxt["issue"] else "Issue:     none (claim with the fallback of tasks.md §1.2)")


if __name__ == "__main__":
    main()
