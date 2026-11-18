---
id: TASK-62
title: Refuse a switch proof that never named the requested model
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T23:07:00Z'
updated_date: '2026-11-18T03:46:00Z'
labels:
  - concurrency
  - wire
dependencies:
  - TASK-66
priority: high
ordinal: 62000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The router could vouch for a model nothing ever reported: on the `wait_health = false` path a successful `/models` fetch was published as a proof and the switch ended ready whether or not the reported list named the requested model, and a **failed** refresh served the request outright. The owner answered: refuse (decision-27). The refusal landed; this row owns its shape — a reason a client can branch on, a feed record the engine cannot lengthen at will, and proof at the HTTP surface.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 a refresh whose reported list omits the target ends switch_failed with an actionable reason, Healthy=false, and no Ready
- [x] #2 the wire wording (RouterCore.cs:542-551) and the feed event change land in the same commit
- [x] #3 a positive control pins the wait_health=false success path, and the failed-refresh test moves only with TASK-66's answer
- [x] #4 SPEC 5.5 says which name space a /models answer lives in: the omission rule refuses a name differing only by alias or provider id
- [x] #5 The refusal reason is bounded in length as well as in name count, keeping the count of what was omitted
- [x] #6 One request-side test walks the refusal: an aliased model gets switch_failed with no inference dispatch, the feed names the near miss, and the capability reads unknown
- [ ] #7 The caller-facing reason is one of a named finite set of categories, with no model name or window number in it; the parameters stay on the feed, pinned to exact strings
- [ ] #8 Every identifier interpolated into a feed reason is bounded before the string is built, a pin bounds the whole reason with eight long names quoted, and the docs call the feed text a bounded excerpt
- [ ] #9 The request-side pin asserts the HTTP response — status, the one lowercase error shape, and the category — not only the in-process exception
- [ ] #10 One fairness case still runs the trust path with an answering `/models` refresh
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Replace the interpolated categories with fixed tokens (`refresh_omits_model`, `refresh_unanswered_model`, `window_too_small`, `launch_failed`, `health_timeout`, `switch_error`), keep the model name, engine name and window numbers on the feed, and bound `model` and `_def.Name` before any `Emit` builds a sentence. Re-point the existing pins at the exact tokens, assert the 503 body at the HTTP layer, and restore one trust-path fairness case. Cheap while unpublished: no release tag exists, so these strings are not published history.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**AC #1–#6 landed at [3dce284](https://github.com/ArborealML/Inference-Router/commit/3dce284) (v0.12.33), CI green**; #1/#3/#4 at ab54d27. The caller gets a bounded reason *category*, the quoted list caps at eight with each cut name's true length marked, and the near-miss end-to-end pin proves 503 with zero upstream calls. The commit also carried the SPEC §15 line TASK-67 AC #5 owed.

**Commit review ingested (deep tier, REVISE), each finding checked before use**; it became AC #7–#10. The caller-facing reason interpolates a model key and `window_<n>_lt_<n>` (`EngineRuntime.cs:1040`), so it is not a finite set. `Emit` interpolates `model` and `_def.Name` outside every cap, and the length pin quotes one long name of thirty. The request-side pin stops at `RouterRequestException`, with no assertion of a refusal body at the wire. Both `SwitchFairnessTests` cases moved to the prove-health path with a reported window (4096 → 32768), so trust-path fairness is unpinned. **Accepted gap:** the new pin runs `/bin/true`, so it is Unix-only; the test matrix is `ubuntu-24.04` and only the release workflow builds `windows-2022`, so no leg breaks today — record it in SPEC §15 with the fix.
<!-- SECTION:NOTES:END -->
