---
id: TASK-15
title: Verify the macOS release legs on Actions
status: In Progress
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-16T22:30:00Z'
updated_date: '2026-11-17T07:28:00Z'
labels:
  - ci
milestone: v0.9 - native engine lifecycle
dependencies:
  - TASK-13
  - TASK-22
ordinal: 15000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The published surface is two platforms: decision-10 (accepted) chose to drop the macOS legs rather than buy runner minutes. What is left is implementation, and it is the repo session: release.yml still declares osx-arm64 (macos-14) and osx-x64 (macos-13), its publish gate requires four verified assets, the SHA256SUMS matrix enumerates four RIDs, and the README still tells readers that macOS binaries are coming. Until those say two, the repository claims artifacts it will never produce.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 decision-10 is implemented: the workflow stops declaring osx legs and stops gating on four verified assets
  documented warning (owner decision)
- [ ] #2 The SHA256SUMS matrix and the README platform copy name linux-x64 and win-x64 and nothing else
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Blocked on TASK-22 — the next move is macOS runner minutes and the release tag, which belongs to a human. Work here resumes when that task is closed.

Blocked-state check: this task is blocked because a person has to grant something (macOS runner minutes) and agree the first tag; the human task that carries both answers is TASK-22, now also recorded as a dependency so the board's own dependency view agrees with the prose.

Rewritten to the drop branch. AC 1 is closed by citing decision-10 rather than restating it; the old decision criterion and the minutes question are gone, because TASK-22 is Done and its answer was no paid minutes. Line anchors: release.yml lines 115-122 (legs) and 216-222 (publish gate); README lines 82-84 and 219-222.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T06:00:00Z

---

The decision in AC #1 is taken: **drop the macOS legs** — no paid runner minutes (decision-10). Two criteria removed rather than left as holes on a resolved task: the "if kept, one dry run completes both macOS legs green" branch can never be reached now, and "the runner image names are current for the account" stopped mattering the moment the legs went away (worth recording that they were never exercised, so nobody re-adds a leg from memory of the workflow file). What remains is AC #3 and it is code and documentation, not a decision: the workflow stops declaring and gating on osx artifacts, and the README platform table, the download copy and the checksum matrix name two platforms instead of four. Assignee is the code session because the next move is that edit.

---
<!-- COMMENTS:END -->
