---
id: TASK-44
title: Close the open release-gate review findings (SPEC v0.12.15 onward)
status: Waiting
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T10:52:00Z'
updated_date: '2026-11-17T16:01:00Z'
labels:
  - ci
  - release
  - security
dependencies: []
priority: high
type: task
ordinal: 43000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Wake: the plan review of the amended release-control sequence, dispatched by the coding session after the thirteenth code review; the commit review follows when its code lands.** Nothing here is blocked on a person.

**State of play.** The release gate has never run against a real tag: that proof is TASK-42's canary, not more YAML, and no tag is cut while this task is open. Two criteria stay open because only a live run can close them — whether `contents: write` from `GITHUB_TOKEN` really suffices for asset upload plus the un-draft `PATCH`, and reading `.immutable` on a published release, which waits on TASK-45. **Do not cut a tag while this task is open.**

**Round discipline.** One row per review in SPEC §15, one line per finding's disposition, the revision-log line in the same commit as the fix. Thirteenth round: two P2s (an outer truncation still cuts a long `upload_url`'s tail; a string `"false"` prints like boolean `false`) plus a harness gap — `diag_fields_survive` proves nothing while its fixture keeps sibling fields short. Twelve earlier rounds are cited by SPEC version and sha, not repeated here.

**Harness counts come from the tracked harness and from CI, not from a local run**: `python3 tools/relgate.py`, which CI also runs as its own job on `main` with a full checkout; the count series lives in git and each round names the case count and the CI job rather than a board session's own run.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 the twelve closed criteria live in [SPEC 15](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md) as the rows 'Deep review of v0.12.15' through 'Twelfth deep review of the release gate', and in the commits 95410ba, 8f78a73 and 79a21e0
- [ ] #2 the two authorization facts stay unproven until a live run: `GITHUB_TOKEN` at `contents: write` sufficing for upload plus un-draft, and the workflow-write guard for a tag at the head of main
- [ ] #3 the gate reads `.immutable` on a published release and records it, once immutable releases are on (behind TASK-45)
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**Current round only.** Thirteenth review over 79a21e0, recorded before its fix exists: the two P2s above are reproduced at source, and the `diag_fields_survive` fixture is the gap. AC count stays where it is until a fix and a fixture that fails without it both land.

**The census behind the stderr criterion, retired into SPEC 15 with the v0.12.20 round** (re-run: `git show main:.github/workflows/release.yml | grep -c '2>/dev/null'`): 9 sites discard stderr, 6 jq shape guards discard stdout only, 1 discards both, 1 API read captures stderr to a temp file removed by an EXIT trap and reports only the exit code plus status-like numbers, 1 merge is compiler output (`dotnet publish … 2>&1 | tee aot-<rid>.log`). jq prints a value only for typed operations, truncated to ten characters, and every typed operation in those six guards sits behind a `type` guard — pinned by `shape_jq_stderr`. Text that says "sanitized stderr" describes the replaced rule.

**Rejected, and still rejected:** the case-fold finding — the replaced code folded the same prefix (`${got_prefix,,}`), deliberately, since v0.12.15. Three limits stay limits, not tasks: an undetectable short array, a duplicate key defeating the tag match, the `< 2^53` ceiling refusing a future id on purpose. The release-write race is never claimable from YAML; its controls are TASK-38 and TASK-45.

**Owed by the coding session:** the SPEC §15 rows that let the twelve closed criteria and their round summaries retire out of this task, which is what keeps it inside budget.
<!-- SECTION:NOTES:END -->
