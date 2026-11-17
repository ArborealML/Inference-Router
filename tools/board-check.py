#!/usr/bin/env python3
"""Check backlog/ against the task-content rules in AGENTS.md.

Repo-tracked on purpose: the board's own scripts live in .local/ and are not readable by
anyone else, so a "0 defects" claim there cannot be audited. This one is the contract's
gate. Word budgets are reported and only fatal with --strict-budget, so the corpus can be
brought under the limit without turning every other check green-or-red at the same time.

Usage: python3 tools/board-check.py [--strict-budget] [--root PATH]
Exit 0 with no defects; 1 with defects; 2 if the board cannot be read at all.
"""
from __future__ import annotations

import argparse
import collections
import datetime
import re
import subprocess
import sys
from pathlib import Path

RENDERED = {
    "Description",
    "Acceptance Criteria",
    "Definition of Done",
    "Implementation Plan",
    "Implementation Notes",
    "Comments",
}
STATUSES = {"To Do", "In Progress", "Waiting", "Blocked", "Done"}
BODY_LIMIT = 600
SECTION_LIMIT = 300
DESC_LIMIT = 300
AC_ITEM_LIMIT = 40
AC_MAX = 14
AGENT_HANDLES = {"qwen3.8-flash-next", "qwen3.8-flash-next-board", "chatswarm"}

# A body that denies a person is in the path cannot sit in Blocked: Blocked means a human
# owes a decision (AGENTS.md, Kanban columns).
NO_PERSON = re.compile(
    r"no (owner|person|human) (keystroke|is in this path|acts? here|needed)|"
    r"agent-run|not blocked|no approval (is )?needed",
    re.I,
)
HANDLE = re.compile(r"@([A-Za-z0-9._-]+)")
CHECKBOX = re.compile(r"^\s*[-*] \[[ xX]\] ")
HEADING = re.compile(r"^## +(.+?)\s*$")
# A short hex token next to a commit cue. File digests in this board are written truncated
# ("sha256 b02b6d32…97e9"), which is not a commit and must not be looked up in git.
SHA_TOKEN = re.compile(r"(?<![0-9a-f])(?=[0-9a-f]{7,9}\b)(?=[0-9a-f]*[a-f])[0-9a-f]{7,9}(?!…)")
COMMIT_CUE = re.compile(r"commit|pushed|landed|land|at\s+$|head\b|parent|reverts?|merge", re.I)
EVIDENCE = re.compile(
    r"blob/main/|pull/\d+|\b[0-9a-f]{7,9}\b|decision-\d+|TASK-\d+|SPEC\.md|SPEC §|release\.yml|ci\.yml|"
    r"backlog/(tasks|decisions|archive)/",
    re.I,
)
DISPATCHED = re.compile(r"dispatch|request (id|landed|completed)|\d{2}:\d{2}(:\d{2})?Z", re.I)
WAKE = re.compile(r"\bwake\b|\bwoken\b|\bwakes\b|not until|only after", re.I)


def strip_markers(text: str) -> str:
    return re.sub(r"<!--[^>]*-->", " ", text)


def body_of(raw: str) -> tuple[dict[str, str], str]:
    parts = raw.split("---\n", 2)
    if len(parts) < 3 or not parts[0].strip() == "":
        return {}, ""
    front, body = parts[1], parts[2]
    fields: dict[str, str] = {}
    key = None
    for line in front.splitlines():
        m = re.match(r"^([a-z_]+):\s*(.*)$", line)
        if m:
            key, val = m.group(1), m.group(2).strip()
            fields[key] = val if val else ""
        elif key and re.match(r"^\s+-\s", line):
            fields[key] = (fields[key] + " " + line.strip()[1:].strip()).strip()
    return fields, body


def sections(body: str) -> dict[str, list[str]]:
    out: dict[str, list[str]] = {}
    cur = "(before first heading)"
    out[cur] = []
    for line in body.splitlines():
        m = HEADING.match(line)
        if m:
            cur = m.group(1).strip()
            out.setdefault(cur, [])
        else:
            out[cur].append(line)
    return {k: v for k, v in out.items()}


def ac_block(body: str) -> tuple[str, list[str]]:
    """The text between the AC markers, and the criteria inside it."""
    chunks = re.findall(r"<!--\s*AC:BEGIN\s*-->(.*?)<!--\s*AC:END\s*-->", body, re.S)
    items = [CHECKBOX.sub("", line).strip() for c in chunks for line in c.splitlines() if CHECKBOX.match(line)]
    return "\n".join(chunks), items


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--strict-budget", action="store_true", help="make word budgets fatal")
    ap.add_argument("--root", default="backlog")
    args = ap.parse_args()

    tasks_dir = Path(args.root) / "tasks"
    if not tasks_dir.is_dir():
        print(f"cannot read {tasks_dir}", file=sys.stderr)
        return 2

    defects: list[str] = []
    notes: list[str] = []
    open_words: list[tuple[int, str]] = []
    verified: set[str] = set()

    def bad(name: str, rule: str, detail: str) -> None:
        defects.append(f"{name}: {rule}: {detail}")

    files = sorted(tasks_dir.glob("*.md"))
    if not files:
        print(f"no task files under {tasks_dir}", file=sys.stderr)
        return 2

    titles: dict[str, str] = {}
    for path in files:
        raw = path.read_text(encoding="utf-8")
        fields, body = body_of(raw)
        if not body.strip():
            bad(path.name, "PARSE", "no body after front matter")
            continue
        tid = fields.get("id") or path.name.split(" - ")[0]
        status = fields.get("status", "")
        title = " ".join(fields.get("title", "").split())
        if status not in STATUSES:
            bad(tid, "STATUS", f"unknown status {status!r}")
        assignees = HANDLE.findall(fields.get("assignee", ""))
        if not assignees:
            bad(tid, "ASSIGNEE", "no assignee in front matter")

        secs = sections(body)
        heads = [m.group(1).strip() for m in map(HEADING.match, body.splitlines()) if m]
        for heading in set(heads):
            if heading not in RENDERED:
                bad(tid, "HEADING", f"## {heading!r} is not rendered by the board tools")
        for heading, n in sorted(collections.Counter(heads).items()):
            if n > 1:
                bad(tid, "HEADING", f"## {heading!r} appears {n} times")

        # criteria placement
        begins = len(re.findall(r"<!--\s*AC:BEGIN\s*-->", body))
        ends = len(re.findall(r"<!--\s*AC:END\s*-->", body))
        if begins != 1 or ends != 1:
            bad(tid, "ACBLOCK", f"{begins} AC:BEGIN and {ends} AC:END markers, expected one pair")
        ac_text, ac_items = ac_block(body)
        ac_count = len(ac_items)
        outside = [
            line.strip()[:70]
            for line in body.splitlines()
            if CHECKBOX.match(line) and line not in ac_text.splitlines()
        ]
        if outside:
            bad(tid, "ACBLOCK", f"{len(outside)} checkbox(es) outside the AC block, e.g. {outside[0]!r}")
        checked = sum(1 for line in body.splitlines() if re.match(r"^\s*[-*] \[[xX]\] ", line))
        if status == "Done" and ac_count != checked:
            bad(tid, "DONE", f"{ac_count - checked} unchecked of {ac_count} criteria")
        if status != "Done" and ac_count and ac_count == checked:
            bad(tid, "STATUS", f"all {ac_count} criteria checked but status is {status!r}")
        if status != "Done":
            if ac_count > AC_MAX:
                bad(tid, "ACCOUNT", f"{ac_count} criteria; more than {AC_MAX} means two tasks")
            for i, item in enumerate(ac_items, 1):
                words = len(strip_markers(item).split())
                if words > AC_ITEM_LIMIT:
                    bad(tid, "ACWORDS", f"criterion #{i} is {words} words (limit {AC_ITEM_LIMIT})")

        # word budgets (reported always; fatal only with --strict-budget)
        body_words = len(strip_markers(body).split())
        if status != "Done":
            open_words.append((body_words, tid))
            for label, limit, got in (
                ("BUDGET", BODY_LIMIT, body_words),
                ("SECTION", SECTION_LIMIT, max(len(strip_markers("\n".join(v)).split()) for v in secs.values())),
                ("DESC", DESC_LIMIT, len(strip_markers("\n".join(secs.get("Description", []))).split())),
            ):
                if got > limit:
                    where = f"{label}: {got} words (limit {limit})"
                    if args.strict_budget:
                        bad(tid, where.split(":")[0], where.split(": ", 1)[1])
                    else:
                        notes.append(f"{tid}: {where} [budget not enforced]")

        # status against its wake or blocker
        first_line = next((ln for ln in secs.get("Description", []) if ln.strip() and not ln.strip().startswith("<!--")), "")
        if status == "Blocked":
            if NO_PERSON.search(first_line) or NO_PERSON.search(" ".join(secs.get("Description", []))[:400]):
                bad(tid, "STATUS", "Blocked, but the body says no person is in this path")
            if not (set(assignees) - AGENT_HANDLES):
                bad(tid, "STATUS", "Blocked, and no human assignee: a task that waits on another task belongs in Waiting naming it")
        if status == "Waiting" and not WAKE.search(body):
            bad(tid, "STATUS", "Waiting with no named wake event")

        # evidence on closed work
        if status == "Done" and not EVIDENCE.search(body):
            bad(tid, "EVIDENCE", "closed work names no commit, pull request, SPEC section or file")
        if status == "Done":
            cued = set()
            for m in SHA_TOKEN.finditer(body):
                window = body[max(0, m.start() - 40):m.start()]
                if COMMIT_CUE.search(window) and "sha256" not in window[-14:]:
                    cued.add(m.group(0))
            for sha in sorted(cued):
                if sha not in verified:
                    probe = subprocess.run(
                        ["git", "cat-file", "-e", f"{sha}^{{commit}}"], capture_output=True
                    )
                    if probe.returncode != 0:
                        bad(tid, "EVIDENCE", f"cited {sha} is not a commit in this repository")
                    verified.add(sha)

        # external service on a task means a request was dispatched
        if "chatswarm" in assignees and not DISPATCHED.search(body):
            bad(tid, "DISPATCH", "assigned to @chatswarm with no dispatched request named in the body")

        key = re.sub(r"[^a-z0-9]+", " ", title.lower()).strip()
        if key and key in titles:
            bad(tid, "DUPLICATE", f"same title as {titles[key]}")
        elif key:
            titles[key] = tid

    open_words.sort()
    total = sum(w for w, _ in open_words)
    median = open_words[len(open_words) // 2][0] if open_words else 0
    print(f"corpus: {len(open_words)} open task(s), {total} words total, median {median}, "
          f"max {open_words[-1][0] if open_words else 0} ({open_words[-1][1] if open_words else '-'})")
    print(f"corpus as-of {datetime.datetime.now(datetime.timezone.utc):%Y-%m-%dT%H:%MZ}; "
          f"budgets {'enforced' if args.strict_budget else 'reported only (pass --strict-budget to enforce)'}")
    for n in notes:
        print(f"note: {n}")
    if defects:
        for d in defects:
            print(f"defect: {d}")
    print(f"{len(defects)} defect(s) across {len(files)} task file(s)")
    return 1 if defects else 0


if __name__ == "__main__":
    sys.exit(main())
