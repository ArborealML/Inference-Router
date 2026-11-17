---
id: TASK-37
title: Close the four holes a second review found in the release gate (v0.12.10)
status: Done
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T08:48:00Z'
updated_date: '2026-11-17T09:11:00Z'
labels:
  - ci
  - release
  - security
dependencies: []
priority: high
ordinal: 36000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Carrier for the third hardening round of the release gate, so a review finding has a home instead of living in a
commit message. Round 1 (the gate that closed TASK-15) is recorded on TASK-36 and stays Done; this is the round
where a **second** review showed that the hardened gate still passed forged artifacts, and the coding session
reproduced each hole before fixing it — four forgeries passed the previous gate.

**The four holes, as reproduced:** an artifact directory named `asset-linux-x64 win-x64` was admitted because the
leg match used a substring test instead of a word match; a Windows archive whose only member was `airXexe` was
accepted because `grep -qx "air.exe"` treats the dot as a regex wildcard; an archive holding `air.exe` plus a
stray `payload.dll` was accepted because nothing constrained the member count; and a **symlink** named
`air-linux-x64.tar.gz` slipped past `find -maxdepth 1 -type f` and was dereferenced by `cp`, while a **directory**
wearing that name passed the name checks (because `test -s` is true for directories) and only died later. A fifth
finding was not artifact-borne and was the most serious: the cleanup step guarded deletion by comparing the draft's
`created_at` against `github.run_started_at`, a context property the coding session could not confirm exists. Read
as empty, the comparison is a no-op and the step deletes **whoever else's draft happens to carry the tag**.
Unverifiable is enough here: an unpublished guard is not a safe guard.

**What landed** in [6b91de4](https://github.com/ArborealML/Inference-Router/commit/6b91de4) (SPEC v0.12.10, the §14
published-surface bullet and the README paragraph in the same commit; the SPEC status header, which still read
v0.12.8, corrected): word-equality leg matching; an inventory that walks **every** entry and requires exactly two,
each a regular non-symlink file with content; exactly one tar member, matched literally with `grep -Fqx`, with the
binary name stated per RID instead of inferred from a `win-*` prefix; the tag re-resolved to a commit and compared
against the commit the legs built before anything is created (release.yml:325-329, because `--verify-tag` only
proves a tag exists); every remote asset required to be in `uploaded` state at the staged file's byte size
(361-364); the draft addressed by the id `gh release create` returned (335-340), with cleanup deleting only that id
and only while it is still an unpublished draft of that tag (385-395); and preflight paginating the release list and
reporting more than one release on a tag as a conflict rather than picking the first hit (90-99).

**Board verification, by reading the checked-out step bodies — the executions are not mine:** each mechanism above
is present at the lines named. The behaviour-level proof is the tracked harness, `tools/relgate.py` — 33 cases at the time, zero failures on 6b91de4 and exactly the four forgeries failing on 6aa26cc, history at those commits; the same file runs as its own CI job on `main`, so current counts come from that job and the external reviews. CI for
6b91de4 on main: completed, success.

**One criterion is deliberately left open.** The accepted gap — the gate verifies the remote asset's name, state and
size but never downloads and hashes the served bytes, so damage inside GitHub's own storage is the downloader's
sidecars to catch — is currently written into [SPEC §14](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md#L1346)
and the revision log, not as a disposition row in SPEC §15. The working contract puts accepted gaps from reviews in
§15, so until that row exists this task is not closed.

**Wake condition:** the external deep review of 6b91de4 landing, plus the §15 row. Nothing else on the board waits
on this task; TASK-28 (the first real tag) waits on the owner's go-ahead, and its first-run checklist is where the
gate finally gets proven.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 An artifact directory is matched to a declared leg by word equality, so a name like 'asset-linux-x64 win-x64' is refused
- [x] #2 The artifact inventory walks every entry and requires exactly two, each a regular file that is not a symlink and is not empty
- [x] #3 Each archive holds exactly one member, matched as a literal name, with the binary name stated per RID rather than inferred from a prefix
- [x] #4 The tag is re-resolved to a commit and compared against the commit the legs built before anything is created
- [x] #5 Every remote asset must be in uploaded state at the staged file's byte size before the release is un-drafted
- [x] #6 Cleanup deletes only the release id this run created, and only while it is still an unpublished draft of this tag; with no captured id it deletes nothing
- [x] #7 Preflight paginates the release list and reports more than one release on a tag as a conflict
- [x] #8 The accepted gap - the gate inspects the remote asset's name, state and size but never downloads and hashes the served bytes - is written up as a disposition in SPEC 15, not only as SPEC 14 copy and a commit message
<!-- AC:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T08:48:00Z

---

Seven boxes checked on inspection of the checked-out workflow (lines named in the description), not on execution: the forgeries were reproduced and killed by the coding session's harness, which is their evidence and is credited as such. AC #8 stays open on a real shortfall, not a formality: the accepted gap is recorded in SPEC 14 (published surface, around line 1346) and in the revision log, but the working contract puts accepted gaps from reviews in SPEC 15, and there is no row there for it. CI for 6b91de4: completed, success. Wake: the external review of 6b91de4 landing plus that row.

---

created: 2026-11-17T09:10:00Z

---

Closed. AC #8 verified, not taken on the report: SPEC 15 now carries the row at line 1445, Deep review of v0.12.9-v0.12.11, which states the accepted gap (the gate compares name, uploaded state, byte size and GitHub's reported stored-bytes digest against the staged file, but never downloads and hashes the served bytes) and points at the published sidecars as the downloader's check. 14's published-surface bullet and the README CI paragraph say the same in operator language. TASK-36 stays Done as round 1; the next round is TASK-38.

---

created: 2026-11-17T09:11:00Z

---

Closed. AC #8 verified, not taken on the report: SPEC 15 now carries the row at line 1445, Deep review of v0.12.9-v0.12.11, which states the accepted gap (the gate compares name, uploaded state, byte size and GitHub's reported stored-bytes digest against the staged file, but never downloads and hashes the served bytes) and points at the published sidecars as the downloader's check. SPEC 14's published-surface bullet and the README CI paragraph say the same in operator language. TASK-36 stays Done as round 1; the next round is TASK-38.

---
<!-- COMMENTS:END -->
