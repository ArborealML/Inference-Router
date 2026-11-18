---
id: TASK-15
title: Drop the macOS release legs; publish linux-x64 and win-x64
status: Done
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-16T22:30:00Z'
updated_date: '2026-11-18T19:29:00Z'
labels:
  - ci
  - release
milestone: m-9
dependencies: []
priority: medium
ordinal: 15000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
DECIDED: the macOS legs are dropped, not funded — decision-10 is accepted. The release surface is linux-x64 and win-x64; the repository must stop advertising osx artifacts it has never built. What is left is implementation, not a decision: remove the two osx matrix legs from the release workflow, move the publish gate from four verified assets to two, correct the checksum matrix, and fix the README platform copy. Nothing here needs paid runner minutes, and no macOS binary is published; a mac user builds from source.

**Verdict.** Done on 713ffdd and fbc48d6: the osx matrix legs are gone, the publish gate counts two archives and checks their bytes, README and SPEC 14 name linux-x64 and win-x64 only, and the Cli project keeps four RIDs so a local macOS build stays possible. Authority: decision-10. No published macOS artifact exists or is claimed.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Closed by decision-10: the decision to drop, fund, or publish unverified is taken (drop)
  documented warning (owner decision)
- [x] #2 The release workflow builds and smoke-tests only linux-x64 and win-x64 on their own runners, and the publish gate counts two verified assets
- [x] #3 The workflow, the checksum matrix and the README stop claiming osx artifacts
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

created: 2026-11-17T07:45:00Z

---

Closed on my own re-check of 713ffdd, not on the report. What I ran: the workflow parses to matrix legs exactly [linux-x64, win-x64]; the only remaining osx/macOS mentions under .github/workflows are the four explanatory comment lines at 115-118, no leg and no gate; the asset gate loop is now 'for rid in linux-x64 win-x64' with a test -f per archive and SHA256SUMS written from air-*.tar.gz; README lines 83-85 and 229 name two published platforms and keep '-r osx-arm64' as a local build; SPEC v0.12.7 plus the new published-surface bullet in 14 agree. CI for 713ffdd on main: completed success (ci, 1m58s). Not mine: the gate snippet was exercised against fake out/ archives by the main session (exit 1 missing, exit 0 with both) - recorded as their evidence, not repeated here. Dependency on TASK-22 removed, which the 07:30 comment claimed and had not done: the decision is decision-10 and TASK-15 no longer waits on a closed task.

---

created: 2026-11-17T07:53:00Z

---

Re-checked at HEAD fbc48d6 rather than at 713ffdd, since AC #2 and #3 are claims about the workflow as it stands and fbc48d6 hardened the gate after the close: matrix legs still parse to [linux-x64, win-x64]; the gate names the same two in expected_rids (216), refuses an archive count that differs from the declared leg count (225), and a -suffix tag now publishes as a prerelease (241), which closes the hole where preflight admitted such tags and GitHub listed them as final. README and SPEC 14 now describe the surface as configured rather than proven, which is accurate: no tag has ever been published, so the gate has never run end to end - that is TASK-28, not a claim here. Two external deep reviews cover 713ffdd and fbc48d6; both in flight or consumed as fixes-forward by the main session, and per the contract they change neither this task's status nor its assignee.

---

created: 2026-11-17T08:18:00Z

---

Opened and closed by the board on the same pass, at the coding session's request, so a review finding has a carrier instead of living in a chat message. Board question answered: option (a), a task of its own, because TASK-15's three criteria are true and unchanged - the hardening is work in response to a review of Done work, which the contract says is recorded as a next commit plus a carrier, not as a reopened task.

---

created: 2026-11-17T08:22:00Z

---

Board accident, disclosed: while opening TASK-36, one edit was issued with an empty task id and landed here - it replaced this Description with TASK-36's text and overwrote the AC block. Repaired from the committed version at HEAD: the original description, the three original criteria and their checks are restored, and the verdict paragraph is re-appended where it renders. Nothing else in this task was touched, and TASK-36 carries the gate-hardening content correctly. Cause recorded as a tooling trap: never pass a task id from a piped create without checking it is non-empty.

---
<!-- COMMENTS:END -->
