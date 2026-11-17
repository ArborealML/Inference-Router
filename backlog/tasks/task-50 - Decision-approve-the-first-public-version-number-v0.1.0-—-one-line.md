---
id: TASK-50
title: 'Decision: approve the first public version number (v0.1.0) — one line'
status: Blocked
assignee:
  - '@claire'
created_date: '2026-11-17T15:01:00Z'
updated_date: '2026-11-17T16:00:00Z'
labels:
  - release
dependencies: []
priority: high
type: task
ordinal: 50000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Answer in two lines, about 2 minutes: `v0.1.0: go | hold`, and `ratify decision-21: yes | no`.** This is the release in-tray's only remaining question of principle; the mechanics are all agent-owned and already scheduled.

**What `go` changes.** A version number becomes public for the first time: the tag is cut, the release workflow runs unattended, five artifacts plus `SHA256SUMS` go out, and the repository becomes installable by version. With immutable releases on (TASK-45) that name can never be reused, even if the release is deleted, so `go` also ends the rehearsal argument. Nothing is signed or attested today, and the gate has never run against a real tag — that is the honest state, and a `go` makes the first run the proof.

**What `hold` changes.** The gate gets its rehearsal instead: the canary (TASK-42) runs first, in a throwaway repository if TASK-49 says yes, and the first public version waits for that evidence. Cost: one more Actions run of the same two legs (Linux and Windows; decision-10 keeps macOS off), and no public number until the rehearsal reports.

**The second line is about who decides at all.** decision-16 handed publishing acts to the agent, reportably; decision-21 asks for your go-ahead first. The board cannot take back what you granted, so decision-21 stands pending your ratification. `yes` keeps the gate on this task; `no` means decision-16 governs and TASK-32 needs no approval at all — the agent cuts the tag and reports it.

**Costs**: about 20-40 runner-minute-equivalents per full run, measured as an estimate; the account's included minutes are not readable with the working token.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 the answer `v0.1.0: go | hold` is recorded here; a `go` releases the tag task and a `hold` parks it — this task performs no act
- [ ] #2 the answer `ratify decision-21: yes | no` is recorded, and decision-21's status is changed to match
<!-- AC:END -->
