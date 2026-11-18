---
id: TASK-7
title: 'External design review: native engine-lifecycle design'
status: Done
assignee:
  - '@chatswarm'
created_date: '2026-11-16T20:46:00Z'
updated_date: '2026-11-18T19:29:00Z'
labels:
  - external
milestone: m-4
dependencies: []
ordinal: 7000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
An external reasoning pass has been dispatched to produce the design and a file-by-file implementation plan for TASK-1 (porting the operator's selector into AIR, driven by the Docker API rather than the command line). The result is held outside this repository; once reviewed it is written into TASK-1 as its plan before any code is written.

**Result (external design review; full 452 KB artifact stays outside the repo)**
Design deliverable: lifecycle as a **host transaction** — resolve an immutable target; take a
non-blocking campaign lock then a blocking selection lock; prove ownership, admissibility, assets,
qualifications, image pin and privilege; close group admission; drain the admitted cohort; revalidate
resource versions; stop only owned peers; launch the exact target; prove liveness, loaded non-alias
model, exact context, exact slots, required modalities and provider-exec verification; publish
readiness and reopen admission. **No automatic retry, rollback or readiness fallback**; a timeout
retains the target for inspection. Same-target callers join one operation, cancellation detaches only
the waiter, a competing target gets a transient busy result and stays queued, and exact-target
failures are latched so dispatcher wake-ups cannot become retries. Recovery treats the journal as
advisory, re-derives truth from runtime state plus probes, and never adopts or relabels an unowned
resource. Mutations bind **immutable container ids**, never names.

Where it disagrees with the library review: this design prefers the **docker CLI behind the provider
seam** (safety-critical surface smaller), while the library review says the AOT-safe in-process answer
is raw Docker Engine HTTP over the socket with source-generated JSON. Reconciled in favour of raw
HTTP as the first provider (that is what "call Docker in-process" means here; the objection to libraries
is about reflection roots, not HttpClient), with the CLI kept as an operator fallback. Both agree on
the seam, the ownership gate, image pinning without pulls, and never re-execing sudo.

Checked against our code: today a dispatched ticket keeps its engine-slot reservation across a model
switch (`RouterCore` reserves at dispatch, releases in the `finally` after the engine call). That is
defensible for a single-model engine — while a switch is in flight there is nothing else that engine
can serve — but it must become an explicit, non-inference reservation once the router performs drains
itself, or a switch will look like a busy engine to the scheduler. Recorded as a design requirement
rather than a bug.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Design pass reviewed against the ten invariants and the owner's Docker-API direction
- [x] #2 Decision points surfaced to the owner (see decision-1, decision-2)
- [x] #3 Approved plan recorded in TASK-1 before implementation
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Proof: this was a request to the external research and review service, and its answer is mirrored in the repository rather than in a transcript - the disposition row in [SPEC 15 at line 1353](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md#L1353) (native engine-lifecycle design), which is what the code and SPEC follow. Where the answer was not adopted, the reason is in the same row.
<!-- SECTION:NOTES:END -->
