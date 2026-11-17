---
id: TASK-15
title: Verify the macOS release legs on Actions
status: In Progress
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-16T22:30:00Z'
updated_date: '2026-11-17T07:30:00Z'
labels:
  - ci
milestone: v0.9 - native engine lifecycle
dependencies:
  - TASK-22
ordinal: 15000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
DECIDED: the macOS legs are dropped, not funded — decision-10 is accepted. The release surface is linux-x64 and win-x64; the repository must stop advertising osx artifacts it has never built. What is left is implementation, not a decision: remove the two osx matrix legs from the release workflow, move the publish gate from four verified assets to two, correct the checksum matrix, and fix the README platform copy. Nothing here needs paid runner minutes, and no macOS binary is published; a mac user builds from source.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Closed by decision-10: the decision to drop, fund, or publish unverified is taken (drop)
  documented warning (owner decision)
- [ ] #2 The release workflow builds and smoke-tests only linux-x64 and win-x64 on their own runners, and the publish gate counts two verified assets
- [ ] #3 The workflow, the checksum matrix and the README stop claiming osx artifacts
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

created: 2026-11-17T07:30:00Z

---

Body rewritten to the accepted branch. AC #1 is closed by decision-10 and cites it rather than restating it; the old wording (paid minutes plus a tag) contradicted an accepted decision and is gone. AC #2 is now the implementation criterion the repo session is working; AC #3 covers the copy and checksum matrix. Deps on TASK-22 removed by the decision: TASK-22 is Done and its answer was the drop.

---
<!-- COMMENTS:END -->
