#!/usr/bin/env python3
"""Check backlog/ against the task-content rules in AGENTS.md.

Repo-tracked on purpose: the board's own scripts live in .local/ and are not readable by
anyone else, so a "0 defects" claim there cannot be audited. This one is the contract's
gate. Word budgets are reported and only fatal with --strict-budget, so the corpus can be
brought under the limit without turning every other check green-or-red at the same time.

Usage: python3 tools/board-check.py [--strict-budget] [--root PATH] [--self-test]
Exit 0 with no defects; 1 with defects; 2 if the board cannot be read at all.
"""
from __future__ import annotations

import argparse
import collections
import datetime
import re
import subprocess
import sys
import urllib.parse
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
# A link into this repository on the main branch. The tools write these, and an archive move or a
# rename leaves the old path behind: it still reads as evidence and resolves as a 404.
LINK_URL = re.compile(r"https://github\.com/[^)\s]+/blob/main/[^\s)]+")
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
# A criterion that asks for an answer, on a task parked in Waiting or Blocked, with no human assigned,
# is a question nobody owes: the task sits there forever. An agent-assigned Decision still To Do is
# fine (decision-14: some answers are derivable from facts), so this fires only on parked work.
ASKS = re.compile(r"^(answer|choose|decide|confirm|approve|go ahead)|yes ?\| ?no|\b[a-d]\)\s*yes", re.I)


def strip_markers(text: str) -> str:
    return re.sub(r"<!--[^>]*-->", " ", text)


# Front matter written by the backlog CLI uses folded block scalars (`title: >-`). A line-only
# parser reads the indicator as the value: the title becomes ">-", which normalises to an empty
# key, and the duplicate-title rule silently skips the file. Twelve of forty-eight open tasks
# are written that way, so this is not a cosmetic parser gap.
BLOCK_SCALAR = re.compile(r"^([a-z_]+):\s*([|>])([+-]?[0-9]*|[0-9]*[+-]?)\s*$")


def body_of(raw: str) -> tuple[dict[str, str], str]:
    parts = raw.split("---\n", 2)
    if len(parts) < 3 or not parts[0].strip() == "":
        return {}, ""
    front, body = parts[1], parts[2]
    fields: dict[str, str] = {}
    key = None
    lines = front.splitlines()
    i = 0
    while i < len(lines):
        line = lines[i]
        m = BLOCK_SCALAR.match(line)
        if m:
            key = m.group(1)
            joiner = " " if m.group(2) == ">" else "\n"
            chunk: list[str] = []
            i += 1
            while i < len(lines):
                if lines[i].startswith("  "):
                    chunk.append(lines[i].strip())
                    i += 1
                    continue
                if not lines[i].strip() and any(x.startswith("  ") for x in lines[i + 1:i + 2]):
                    i += 1
                    continue
                break
            fields[key] = joiner.join(x for x in chunk if x)
            continue
        m = re.match(r"^([a-z_]+):\s*(.*)$", line)
        if m:
            key, val = m.group(1), m.group(2).strip()
            fields[key] = val if val else ""
        elif key and re.match(r"^\s+-\s", line):
            fields[key] = (fields[key] + " " + line.strip()[1:].strip()).strip()
        i += 1
    return fields, body


def title_key(title: str) -> str:
    return re.sub(r"[^a-z0-9]+", " ", title.lower()).strip()


def private_path_patterns(gitignore: Path) -> list[re.Pattern[str]]:
    """Patterns for citations of paths this repository does not publish, read from .gitignore.

    A task that says "the harness is at .local/relgate.py" is unreadable from a clone: the same
    defect as an evidence link that does not resolve on main (AGENTS.md, Repo hygiene).
    """
    pats: list[re.Pattern[str]] = []
    if not gitignore.is_file():
        return pats
    for line in gitignore.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#") or any(c in line for c in "*?"):
            continue
        name = line.strip("/")
        if name and "/" not in name:
            pats.append(re.compile(r"(?<![A-Za-z0-9._/-])" + re.escape(name) + r"/[A-Za-z0-9_./-]*"))
    return pats


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


def private_citations(text: str, patterns: list[re.Pattern[str]]) -> list[str]:
    hits = {m.group(0) for pat in patterns for m in pat.finditer(text)}
    return sorted(hits)


FIXTURE = """---
id: TASK-{n}
title: >-
  {title}
status: To Do
assignee:
  - '@qwen3.8-flash-next'
dependencies: []
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
{body}
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 the criterion fits one line
<!-- AC:END -->
"""


def self_test() -> int:
    """Prove the checker sees what it claims to see, on fixtures, before anyone trusts its zero."""
    import tempfile

    failures: list[str] = []

    def run(files: dict[str, str]) -> tuple[int, str]:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp, "backlog", "tasks")
            root.mkdir(parents=True)
            for name, text in files.items():
                (root / name).write_text(text, encoding="utf-8")
            proc = subprocess.run(
                [sys.executable, str(Path(__file__).resolve()), "--root", str(root.parent)],
                capture_output=True, text=True, check=False)
            return proc.returncode, proc.stdout + proc.stderr

    clean = {"task-1 - A.md": FIXTURE.format(n=1, title="First task", body="Build the thing.")}
    code, out = run(clean)
    if code != 0:
        failures.append(f"clean fixture should pass, exit {code}: {out.strip()[:400]}")

    folded = {
        "task-1 - A.md": FIXTURE.format(n=1, title="Cut the release tag", body="Build the thing."),
        "task-2 - B.md": FIXTURE.format(n=2, title="Cut the release tag", body="Build the other thing."),
    }
    code, out = run(folded)
    if code != 1 or "DUPLICATE" not in out:
        failures.append(f"folded titles should collide and be reported: exit {code}: {out.strip()[:400]}")

    cited = {"task-1 - A.md": FIXTURE.format(
        n=1, title="First task",
        body="Run .local/relgate.py, not src/app.cs, backlog/tasks/x.md or docs/images/a.png.")}
    code, out = run(cited)
    hits = [line for line in out.splitlines() if "GITREF" in line]
    if code != 1 or len(hits) != 1 or ".local/relgate.py" not in hits[0]:
        failures.append(f"one unpublished-path citation expected, got: {hits}")

    linked = {"task-1 - A.md": FIXTURE.format(
        n=1, title="First task",
        body="See [the archived decision](https://github.com/o/r/blob/main/backlog/tasks/task-99%20-%20gone.md).")}
    code, out = run(linked)
    if code != 1 or "LINKPATH" not in out:
        failures.append(f"a link to a path that is not on main should be reported: exit {code}")

    doubled = {"task-1 - A.md": FIXTURE.format(n=1, title="First task", body="Build the thing.")}
    doubled["task-1 - A.md"] = doubled["task-1 - A.md"].replace(
        "- [ ] #1 the criterion fits one line", "- [ ] #1 #1 the criterion fits one line")
    code, out = run(doubled)
    if code != 1 or "ACDUP" not in out:
        failures.append(f"a doubled criterion label should be reported: exit {code}")

    cycle = {
        "task-1 - A.md": FIXTURE.format(n=1, title="First task", body="Build the thing.").replace(
            "dependencies: []", "dependencies:\n  - TASK-2"),
        "task-2 - B.md": FIXTURE.format(
            n=2, title="Second task",
            body="Wake: TASK-1 lands the thing, and only after that does this run."),
    }
    code, out = run(cycle)
    if code != 1 or "DEADLOCK" not in out:
        failures.append(f"a wake sentence that closes a cycle should be reported: exit {code}: {out.strip()[:300]}")

    for f in failures:
        print(f"self-test FAILED: {f}")
    print(f"board-check self-test: {'FAILED' if failures else 'ok'} "
          f"(folded titles, duplicate titles, unpublished paths, link paths, doubled labels, "
          f"wake-sentence cycles, clean fixture)")
    return 1 if failures else 0


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--strict-budget", action="store_true", help="make word budgets fatal")
    ap.add_argument("--root", default="backlog")
    ap.add_argument("--gitignore", default=str(Path(__file__).resolve().parents[1] / ".gitignore"))
    ap.add_argument("--self-test", action="store_true", help="check the checker on fixtures and exit")
    args = ap.parse_args()

    if args.self_test:
        return self_test()

    private = private_path_patterns(Path(args.gitignore))
    repo_root = Path(__file__).resolve().parents[1]
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
    graph: dict[str, tuple[list[str], str]] = {}
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
        # A Description that claims a status the task does not have is a misleading wake: the
        # kanban column says one thing and the text a reader acts on says another.
        if status != "Blocked":
            desc = "\n".join(secs.get("Description", []))
            claim = re.search(r"\*\*Blocked|\bis Blocked\b|Blocked on (TASK-\d+|the owner|one decision)|task (stays|remains|is) Blocked", desc)
            if claim:
                bad(tid, "STATUS", f"status is {status!r} but the Description claims {claim.group(0)!r}")
        if status in {"Waiting", "Blocked"} and not (set(assignees) - AGENT_HANDLES):
            for i, item in enumerate(ac_items, 1):
                if ASKS.search(re.sub(r"^#\d+\s*", "", item)):
                    bad(tid, "STATUS", f"criterion #{i} asks for an answer and no human is assigned")

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

        for hit in private_citations(body, private):
            bad(tid, "GITREF", f"cites the unpublished path {hit} — a clone cannot read it: "
                               f"state the fact in the task, or move the file under a tracked path")

        key = title_key(title)
        if key and key in titles:
            bad(tid, "DUPLICATE", f"same title as {titles[key]}")
        elif key:
            titles[key] = tid

        # A markdown link into the repository must name a file that exists: the tools write these
        # links, and a rename or an archive move leaves the old path behind, which reads as evidence
        # and resolves as a 404.
        for url in set(LINK_URL.findall(body)):
            path, _frag = urllib.parse.urldefrag(urllib.parse.unquote(url.split("/blob/main/", 1)[1]))
            if not path or not (repo_root / path).is_file():
                bad(tid, "LINKPATH", f"links {path}, which is not a file on main")

        for i, item in enumerate(ac_items, start=1):
            if re.match(r"#\d+\s+#\d+\s", item):
                bad(tid, "ACDUP", f"criterion #{i} carries its label twice ({item[:40]!r})")

        deps = re.findall(r"TASK-\d+", re.search(r"(?m)^dependencies:\n((?:  - .+\n)+)", raw).group(1)) if re.search(r"(?m)^dependencies:\n", raw) else []
        open_ac_text = " ".join(it for it, line in zip(ac_items, [l for l in body.splitlines() if CHECKBOX.match(l)]) if "[ ]" in line)
        # A task can also close a cycle in prose: its wake sentence names the task that is waiting on
        # it. That is invisible to a rule that reads only the criteria block, and it is the same
        # deadlock — neither side can ever be woken by the other.
        wake_text = " ".join(line for line in body.splitlines() if WAKE.search(line))
        graph[tid] = (deps, open_ac_text, wake_text, status)

    # The same hygiene rule for every other tracked board file: decisions, and the archive the
    # task loop does not walk.
    for md in sorted(Path(args.root).rglob("*.md")):
        if md.parent == tasks_dir:
            continue
        for hit in private_citations(md.read_text(encoding="utf-8"), private):
            bad(str(md.relative_to(args.root)), "GITREF",
                f"cites the unpublished path {hit} — a clone cannot read it: "
                f"state the fact in the file, or move the file under a tracked path")

    # An open task that repeats an archived title has an undocumented predecessor.
    archive_dir = tasks_dir.parent / "archive" / "tasks"
    if archive_dir.is_dir():
        archived: dict[str, str] = {}
        for path in sorted(archive_dir.glob("*.md")):
            fields, _ = body_of(path.read_text(encoding="utf-8"))
            key = title_key(" ".join(fields.get("title", "").split()))
            if key:
                archived.setdefault(key, path.name)
        for key, tid in titles.items():
            if key in archived:
                notes.append(f"{tid}: ARCHIVETITLE: title matches archived {archived[key]} — "
                             f"name the predecessor or retitle")

    open_words.sort()
    # Closure cycles: A depends on B, and an open criterion of B asks for what only A can produce.
    # The dependency graph alone looks acyclic; the criteria close it.
    for tid_a, (deps, open_acs, _wake, _st) in graph.items():
        if _st == "Done":
            continue
        for dep in deps:
            if dep not in graph or graph[dep][3] == "Done":
                continue   # a closed task's wake sentence is history, not a live wait
            acs, wake = graph[dep][1], graph[dep][2]
            if re.search(rf"\b{re.escape(tid_a)}\b", acs):
                bad(tid_a, "DEADLOCK", f"depends on {dep}, whose open criterion names {tid_a} — neither can close")
            elif re.search(rf"\b{re.escape(tid_a)}\b", wake):
                bad(tid_a, "DEADLOCK", f"depends on {dep}, whose wake sentence waits on {tid_a} — neither can close")

    total = sum(w for w, _ in open_words)
    if open_words:
        mid = len(open_words) // 2
        if len(open_words) % 2:
            median = open_words[mid][0]
        else:
            median = (open_words[mid - 1][0] + open_words[mid][0]) // 2
    else:
        median = 0
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
