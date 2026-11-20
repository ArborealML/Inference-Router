---
id: TASK-30
title: >-
  Bound the relay against a client that stops reading, and a generation that
  stalls after its first frame
status: Waiting
assignee:
  - '@gpt-6.2-sol-stream-delivery'
created_date: '2026-11-17T07:01:00Z'
updated_date: '2026-11-20T07:56:00Z'
labels:
  - stream
  - transport
milestone: m-1
dependencies:
  - TASK-34
priority: high
ordinal: 29000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wake: disposition of the third stopped-reader soak plan revision. Backpressure handed it to Delivery for independent re-review. It addresses per-profile time budgets, admission of a fifth request after the stalled request retires, and diagnostics with and without observation wrappers. Harness implementation and measurements await approval. Positive defaults also require representative upstream timing replay.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 A stream whose downstream reader stops consuming ends within a stated bound, with the engine slot released and a named error code on the feed
- [x] #2 Buffering held for one stalled reader is bounded by a number that is in config, not a constant, and the bound's default is stated in SPEC - the number and whether it counts frames or bytes come from TASK-34
- [x] #3 A generation silent past the stated idle bound after its first frame ends in band with SPEC 6's committed-stream shape: one error frame plus `[DONE]`, no new truncation format
- [x] #4 The bound and its code name come from TASK-34's adopted values; the behaviour is pinned by `RequestPhaseTimeoutTests` and named in the SPEC 14.1 row
- [ ] #5 SPEC 14.1 row 2 no longer lists this as open, and a test pins each of 1-3 without a real engine
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
For a positive byte budget, count queued bytes and bytes awaiting flush. Pause upstream reads when credit runs out and resume on returned credit. Abort a pending write after 30 seconds without progress. Independently enable the per-engine idle timer for a pending upstream read after the first raw byte. Exclude time spent waiting for downstream credit. Transfer only validated event bytes and commit successful EOF after the final flush. Retain each stream’s selected configuration across reload and preserve native physical cleanup, opt-out and library behavior.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The [opt-in relay](https://github.com/ArborealML/Inference-Router/pull/45) passed 39 focused and 54 neighboring Release cases, named mutation controls, and warning-free server Native AOT. [Feature merge CI](https://github.com/ArborealML/Inference-Router/actions/runs/44682032652) failed one of 1,620 tests on a premature zero-byte assertion after logical relay exit. [The test-only correction](https://github.com/ArborealML/Inference-Router/pull/46) checks physical credit release separately, passed five focused rows and still detected the deliberate credit leak. The corrected merge ba3c1cc matches the reviewed tree and [passed all hosted jobs](https://github.com/ArborealML/Inference-Router/actions/runs/44683761424). Keep 262,144 bytes and 300,000 ms as proposed defaults pending both measurements.

The proposed soak needs a real stopped HTTP/1.1 socket and an HTTP/2 peer that withholds credit on one stream while another on the same connection stays healthy. The harness must observe reads, flushes, outstanding bytes and memory, and distinguish Kestrel timeouts from router action. The proposed 25-minute exclusive window is unreserved. At 83b43a8 the accessible records contained zero replay-usable streams. The downstream counting stream had no raw timing, four Pi turns had only first-token and terminal timings, and the benchmark had request aggregates. Replay needs authorized per-engine traces with ordered monotonic read gaps, timebase, attribution of pauses caused by downstream backpressure, representative workload counts, completeness and provenance. The soak can proceed independently.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
<!-- COMMENTS:END -->
