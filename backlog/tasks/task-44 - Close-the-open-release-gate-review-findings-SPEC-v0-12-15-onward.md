---
id: TASK-44
title: Close the open release-gate review findings (SPEC v0.12.15 onward)
status: Waiting
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T10:52:00Z'
updated_date: '2026-11-18T19:18:00Z'
labels:
  - ci
  - release
  - security
milestone: m-9
dependencies: []
priority: high
type: task
ordinal: 43000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Wake: the plan review of the amended release-control sequence, dispatched by the coding session after the thirteenth code review; the commit review follows when its code lands.** Nothing here is blocked on a person.

**State of play.** The release gate has never run against a real tag: that proof is a canary run, not more YAML, and no tag is cut while this task is open. What is open here is code, SPEC rows and tests: the thirteenth round's two P2s and the harness gap that hides them. The two authorization facts that only a live run can settle are criteria on the task that performs the run, not here — a task cannot close a criterion it has no act for. **Do not cut a tag while this task is open.**

**Round discipline.** One row per review in SPEC §15, one line per finding's disposition, the revision-log line in the same commit as the fix. Thirteenth round: two P2s (an outer truncation still cuts a long `upload_url`'s tail; a string `"false"` prints like boolean `false`) plus a harness gap — `diag_fields_survive` proves nothing while its fixture keeps sibling fields short. Twelve earlier rounds are cited by SPEC version and sha.

**Harness counts come from the tracked harness and from CI, not a local run**: `python3 tools/relgate.py`, which CI runs
as its own job on `main` with a full checkout; each round names its case count and the CI job.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 the fourteen closed criteria are accounted for: twelve as [SPEC §15](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md) rows 'Deep review of v0.12.15' through 'Twelfth deep review' in 95410ba, 8f78a73 and 79a21e0, and two retired to the task that runs the gate
- [ ] #2 the thirteenth round's two P2s are fixed with a fixture that fails without the fix, and the SPEC §15 row and revision-log line land in the same commit as the code
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**Current round only.** Thirteenth review over 79a21e0, recorded before its fix exists: the two P2s above reproduce at source, and the `diag_fields_survive` fixture is the gap. The AC count moves only when a fix and a failing fixture both land.

**The census behind the retired stderr criterion** lives with the superseded log: [board trim, 2026-11-17](https://github.com/ArborealML/Inference-Router/blob/main/backlog/archive/board-trim-2026-11-17.md),
with the command that re-runs it. It is evidence for a SPEC §15 row, not open work here.

**Rejected, and still rejected:** the case-fold finding — the replaced code folded the same prefix (`${got_prefix,,}`), deliberately, since v0.12.15. Three limits stay limits, not tasks: an undetectable short array, a duplicate key defeating the tag match, the `< 2^53` ceiling refusing a future id on purpose. The release-write race is never claimable from YAML; its controls are TASK-38 and TASK-45.

**Owed by the coding session:** the SPEC §15 rows that let the closed criteria and their round summaries retire out of this task, which is what keeps it inside budget.

**Retired this round, not closed.** Two criteria asked what only a live release can answer — that `GITHUB_TOKEN` at
`contents: write` suffices for asset upload plus the un-draft `PATCH`, and that the gate reads `.immutable` on a published
release. They are criteria on the task whose run performs those acts, and count as retired here. Fourteen criteria are
closed; none waits on a tag.
<!-- SECTION:NOTES:END -->
