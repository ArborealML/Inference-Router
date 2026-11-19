---
id: TASK-62
title: Refuse a switch proof that never named the requested model
status: Waiting
assignee:
  - '@gpt-6.2-sol-config'
created_date: '2026-11-17T23:07:00Z'
updated_date: '2026-11-19T23:21:09Z'
labels:
  - concurrency
  - wire
milestone: m-2
dependencies:
  - TASK-66
priority: high
ordinal: 62000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Waiting for the lead to release the HTTP refusal after the current native swap priorities. The next lead priority review after the pair plan handoff wakes this item. The bounded preparation is preserved. This row owns the shape of the refusal the owner ordered (decision-27): a reason a client can branch on from a closed set, a feed record the engine cannot lengthen, and proof at the HTTP surface.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 a refresh whose reported list omits the target ends switch_failed with an actionable reason, Healthy=false, and no Ready
- [x] #2 the wire wording (RouterCore.cs:542-551) and the feed event change land in the same commit
- [x] #3 a positive control pins the wait_health=false success path, and the failed-refresh test moves only with TASK-66's answer
- [x] #4 SPEC 5.5 says which name space a /models answer lives in: the omission rule refuses a name differing only by alias or provider id
- [x] #5 The refusal reason is bounded in length as well as in name count, keeping the count of what was omitted
- [x] #6 One request-side test walks the refusal: an aliased model gets switch_failed with no inference dispatch, the feed names the near miss, and the capability reads unknown
- [x] #7 The caller-facing reason is one of a named finite set of categories, with no model name or window number in it; the parameters stay on the feed, pinned to exact strings
- [x] #8 Every identifier interpolated into a feed reason is bounded before the string is built, and the docs call the feed text a bounded excerpt
- [ ] #9 The request-side pin asserts the HTTP response — status, the one lowercase error shape, and the category — not only the in-process exception
- [ ] #10 One fairness case still runs the trust path with an answering /models refresh
- [x] #11 A caller-supplied model id is capped in length before it reaches a feed reason or an event payload, and SPEC names the cap
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Compare the remaining HTTP refusal and fairness criteria with landed wire and scheduling fixtures on current main. Identify exact existing evidence and propose only the missing observable control if needed. Preserve the adopted refusal categories.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Landed [610f0d8](https://github.com/ArborealML/Inference-Router/commit/610f0d86d9990e933e676a49e7215dd39f08049e) (v0.12.37): one token from a closed set of seven at the switch exits, the caller sentence built from what the asking caller holds, `WireText` budgets with `Model` stored at reporting length. A high-tier commit review returned AC #7, #8 and #11: `RouterCore.Fail` interpolated the raw `ticket.Model` into a `request.failed` reason, the funnel capped `Model` but not `Reason`, and two exits still hand over free-form prose — `EngineRuntime.cs:620` (a remote engine loads its own models) and `:728-729` (a switch that never started, naming the target). Caps [TASK-93](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-93%20-%20Bound-every-feed-field-at-the-funnel-and-make-SPEC-true-of-it.md), taxonomy [TASK-94](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-94%20-%20Name-every-refusal-category-and-stop-a-fixed-mismatch-reading-as-a-timeout.md).

8e6867d meets `#8` and `#11`: the queue refusal cuts the model id before interpolating, and the funnel caps every producer's identifiers and reason at store. AC #9's harness exists — reuse `ErrorContractTests`' `StartAsync` + `AssertShape`.
<!-- SECTION:NOTES:END -->
