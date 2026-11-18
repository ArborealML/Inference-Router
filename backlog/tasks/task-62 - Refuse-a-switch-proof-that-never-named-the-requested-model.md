---
id: TASK-62
title: Refuse a switch proof that never named the requested model
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T23:07:00Z'
updated_date: '2026-11-18T10:10:00Z'
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
This row owns the shape of the refusal the owner ordered (decision-27): a reason a client can branch on from a closed set, a feed record the engine cannot lengthen, and proof at the HTTP surface.
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
- [ ] #8 Every identifier interpolated into a feed reason is bounded before the string is built, and the docs call the feed text a bounded excerpt
- [ ] #9 The request-side pin asserts the HTTP response — status, the one lowercase error shape, and the category — not only the in-process exception
- [ ] #10 One fairness case still runs the trust path with an answering /models refresh
- [ ] #11 A caller-supplied model id is capped in length before it reaches a feed reason or an event payload, and SPEC names the cap
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Two commits; sites re-measured at `6ea83ca`. **A — src, tests, SPEC.** `state.FailureReason` carries one token from a closed set of seven: `no_select_command` (`EngineRuntime.cs:924`), `launch_failed` (`:954`), `health_timeout` (`:989`), `refresh_omits` (`:1021`), `refresh_unanswered` (`:1043`), `window_too_small` (`:1060`), `switch_error` (`:1077`); nothing else is ever written to that field. `model`, `_def.Name` and the `profile=` name are bounded before any `Emit` sentence is built (`:935`, `:990`, `:1022`, `:1044`, `:1061`); provider text and the reported-list summary keep their caps. Pins walk the set exit by exit; the eight-long-names case bounds the whole `engine.switch_failed` reason and keeps the omitted count. **B — tests only.** AC #9 asserts the 503 body over `WireHarness`; AC #10 restores a trust-path fairness case (`SwitchFairnessTests.cs:64` runs `true, 20s`; the trust path is `false`, as at `SwitchClaimTests.cs:73`).
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Landed [610f0d8](https://github.com/ArborealML/Inference-Router/commit/610f0d86d9990e933e676a49e7215dd39f08049e) (v0.12.37): one token from a closed set of seven at the switch exits, the caller sentence built from what the asking caller holds, and WireText budgets with Model stored at reporting length. **AC #7, #8 and #11 went back** after a high-tier commit review, re-measured at that commit: RouterCore.Fail (:644-647, :828-850) interpolated the raw ticket.Model into a request.failed reason and the funnel capped Model but not Reason; and two exits still hand a caller free-form text outside the seven — EngineRuntime.cs:620 (a remote engine loads its own models) and :728-729 (a switch that never started), the second naming the target. Caps are [TASK-93](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-93%20-%20Bound-every-feed-field-at-the-funnel-and-make-SPEC-true-of-it.md), taxonomy [TASK-94](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-94%20-%20Name-every-refusal-category-and-stop-a-fixed-mismatch-reading-as-a-timeout.md). AC #9 shares its HTTP error-shape harness with the terminal-error bound open on TASK-93: whoever lands first builds it, the other reuses it, and the ceiling is asserted in serialized bytes.
<!-- SECTION:NOTES:END -->
