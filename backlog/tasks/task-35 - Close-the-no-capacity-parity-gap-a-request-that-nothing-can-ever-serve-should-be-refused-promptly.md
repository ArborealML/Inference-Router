---
id: TASK-35
title: >-
  Close the no-capacity parity gap: a request that nothing can ever serve should
  be refused promptly
status: Done
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T08:16:00Z'
updated_date: '2026-11-18T19:29:00Z'
labels:
  - wire
  - scheduling
milestone: m-3
dependencies: []
priority: medium
ordinal: 34000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Carrier for a parity gap that hid as a stray checkbox: TASK-10 carried a criterion — a prompt no-capacity error when
every engine is unhealthy — never implemented, deleted as a stale duplicate rather than ticked.

**Measured current behaviour, corrected against this code at 18:3xZ.** The scheduler answers a request with no eligible
engine as `Wait("no_available_engine")` (PrefixAwareScheduler.cs:102 — not `no_engines`, and the line is 102, not 59).
What the caller then gets depends on whether it declared `max_queue_time`. **With** it, the queue deadline fails the
request as `deadline_exceeded`, which `MapStatus` answers **408** (RouterCore.cs:470, ServerApp.cs:304) — not 503.
**Without** it, no queue timer is armed at all (RouterCore.cs:153) and `MaybeFailDeadline` fires only inside a declared
window, so **the request is never failed**: it sits until the caller disconnects. That hang, not the timing, is the
sharpest form of the gap.

Options unchanged: **fast-refuse** — the caller learns at once, at the cost of failing a request an engine
arriving a second later would have served; **keep waiting** — today's behaviour, at the cost of the hang; **accept the
gap** — write the posture into SPEC §14.1 and §15. The coding
session proposes **fast-refuse, narrowly**, pending the external plan review: refuse with the already-declared 503
`model_unavailable` only when the canonical model appears in **no engine's** configured catalog, which is config-derived
and cannot change while the process runs. TASK-10 AC #9 requires every remaining parity gap to have a named carrier: closing
this one without a disposition reopens it.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 One of fast-refuse, keep-waiting, or accepted-gap is chosen, and the SPEC 14.1 row for it says which, with the reason
- [x] #2 the no-`max_queue_time` hang is either refused or written up as an accepted gap, with a test pinning whichever was chosen
- [x] #3 every error code SPEC §6.1 declares is either emitted by the code or removed from the contract, and the missing `MapStatus` arm for `no_available_engine` is resolved deliberately
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Pending the external plan review; the revised text lands here before code, and the task moves In Progress when the repo
changes. Summary as proposed:

1. `PrefixAwareScheduler` returns `Wait("no_capacity")` when no engine in the view lists the model in
   `LoadedModels ∪ CatalogModels`, with health and switching deliberately ignored — the catalog is config-derived.
2. `RouterCore.TryDispatchOnce` turns that reason into an immediate `Fail(ticket, "model_unavailable", …)` through the
   same remove-by-id plus single-terminal-transition path the queue deadline already uses; other reasons keep waiting.
3. Refusal happens at dispatch, not admission, so enqueue and the event feed stay uniform.
4. Out of scope, its own task: the serving-ladder case (`required_context` fits no profile), because that figure is
   partly an estimate.
5. Tests: catalog, health, empty-catalog and alias cases at scheduler level; the no-`max_queue_time` hang at
   `RouterCore` level; 503 plus `model_unavailable` plus message parity with the feed at wire level.
6. SPEC: a posture bullet in §7 next to the queue-deadline rule, the disposition in §14.1, a §6.1 note that
   `model_unavailable` is now emitted, and a fix for a live contradiction — §7 calls queue overflow "408-mapped" while
   §6.1, ServerApp.cs:303 and EndToEndTests.cs:441 all say 429 `queue_overflow`.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**Two dead strings the fix has to land on.** `model_unavailable` is declared in SPEC §6.1 and mapped to 503
(ServerApp.cs:306) but nothing in the codebase emits it: a code that cannot arrive. `no_available_engine` is emitted as a
wait reason, is absent from `MapStatus` (so it would fall through to 500), and `MaybeFailDeadline` discards its `reason`
argument for `deadline_exceeded` (RouterCore.cs:464-472): the reason a request waited is not the reason it fails.
<!-- SECTION:NOTES:END -->

**Closed at 83ebc75 (SPEC v0.12.25); everything below re-verified in this worktree, not copied from the report.**
Disposition: fast-refuse, narrowly — refused at **admission**, not at dispatch, because dispatch examines only each
model queue's head, so a hopeless ticket behind a stuck head would have gone on hanging (plan step 3 as written here was
wrong; the external review caught it, and the shipped shape is the corrected one). Eligibility is **catalog ∪ models an
engine reports live** through one shared predicate, `EngineRuntime.CanServeModel` (EngineRuntime.cs:108), pinned by
`SchedulerTests.Serves_A_Reported_Model_That_No_Catalog_Entry_Names` — a plan that read the catalog alone would have
refused a serviceable model. Unknown capability is never hopeless: an engine that has never answered `/models` says
nothing, so the request waits while one is unreached, which is what stops a slow engine at startup from producing a 503
for a model it could serve. The no-ceiling case is test-pinned by
`EndToEndTests.A_Model_Nothing_Can_Serve_Is_Refused_At_Admission_With_No_Deadline_Set`. `no_available_engine` is gone from
`MapStatus` and from SPEC §6.1 (measured: nothing ever emitted it), wait reasons are documented as diagnostics that never
reach the wire, and `MaybeFailDeadline` no longer takes a reason argument it discards.

**Not fixed here, and not bugs:** a *legitimate* queued request with no `max_queue_time` still has no ceiling at all, and
the `required_context` fast path was left out because without the header the figure is partly an estimate. Both are
separate tasks now.
