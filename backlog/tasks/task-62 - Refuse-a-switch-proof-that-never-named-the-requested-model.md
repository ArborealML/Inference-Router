---
id: TASK-62
title: Refuse a switch proof that never named the requested model
status: In Progress
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
The router could vouch for a model nothing ever reported: on the wait_health=false path a successful /models fetch was published as a proof and the switch ended ready whether or not the reported list named the requested model — no membership check anywhere on that path. That half was a plain bug and is fixed. The other half was a posture: a **failed** refresh served the request and marked the capability unknown, and the provider interface called that a promise made in configuration rather than a fallback the router may choose. The owner answered it — refuse (decision-27). What is left in this row is the refusal itself: the wire wording, the feed reason, the pinned test that today pins the lenient answer, and the request-side proof that the refusal actually reaches a caller.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 a refresh whose reported list omits the target ends switch_failed with an actionable reason, Healthy=false, and no Ready
- [ ] #2 if TASK-66 changes the posture, the switch_failed wording on the wire (RouterCore.cs:542-551) and the feed event change in the same commit
- [x] #3 a positive control pins the wait_health=false success path, and the failed-refresh test at CapabilityPublicationTests.cs:387-413 moves only with TASK-66's answer
- [x] #4 SPEC 5.5 says which name space a /models answer lives in, because the omission rule refuses a name that only differs by alias or provider id
- [ ] #5 The refusal reason is bounded in length as well as in name count, keeping the count of what was omitted
- [ ] #6 One request-side test walks the refusal: an aliased model gets switch_failed with no inference dispatch, the feed names the near miss, and the capability reads unknown
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Second of the set-1 commits. At :794, **before** any proof is published: if the refresh answered and the reported list does not contain the canonical model (OrdinalIgnoreCase) → `Emit engine.switch_failed` with reason `refresh_omits_<model>`, `PublishUnknown()`, `EndSwitch(state,false)`, return. Checking after `PublishProof` would publish a positive snapshot and burn a version on a switch that is about to be refused. The membership check is the one the prove-health path already makes at :757-759. A **failed** refresh (`refreshed is null`) is untouched: that is the owner's posture question, pinned at CapabilityPublicationTests.cs:405-410. Assumption to state rather than inherit: the omission rule treats the engine's reported names and the catalog's canonical names as one namespace, which `IsReadyFor` already assumes; say so in SPEC. No new wire surface — the refusal reuses the existing switch_failed kind (RouterCore.cs:541-551). Wake condition: the landing of TASK-63 AC #1, whose fenced negative publish this commit follows.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**Landed at [ab54d27](https://github.com/ArborealML/Inference-Router/commit/ab54d27) (v0.12.30): AC #1, #3, #4.** The omission check sits before `PublishProof`, the reason names what the engine reported, and the failed-refresh test is untouched, so the posture stayed the owner's until decision-27 answered it.

The external commit review of that commit is dispositioned here: its checks of the canonical-name comparison, the pre-proof placement, the list-scan cost and the absence of new wire surface all hold against this code. Two of its findings became criteria — the reason is capped in **count** (eight names) and not in length, and no test walked a refusal from the request side. A third, that an omitted name can also mean an asynchronous launch that has not surfaced the model yet, belongs in the reason wording rather than a new rule. Wake condition: main green at HEAD, then AC #2, #5 and #6 in one refusal commit.
<!-- SECTION:NOTES:END -->
