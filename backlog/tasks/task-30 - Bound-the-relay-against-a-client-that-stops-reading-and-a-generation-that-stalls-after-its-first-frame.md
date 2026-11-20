---
id: TASK-30
title: >-
  Bound the relay against a client that stops reading, and a generation that
  stalls after its first frame
status: In Progress
assignee:
  - '@gpt-6.2-sol-stream-backpressure'
created_date: '2026-11-17T07:01:00Z'
updated_date: '2026-11-20T05:00:00Z'
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
Implementing independently configurable HTTP completion-stream protections for a stalled reader and for a silent upstream after its first raw byte. Both settings default to zero (disabled).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A stream whose downstream reader stops consuming ends within a stated bound, with the engine slot released and a named error code on the feed
- [ ] #2 Buffering held for one stalled reader is bounded by a number that is in config, not a constant, and the bound's default is stated in SPEC - the number and whether it counts frames or bytes come from TASK-34
- [ ] #3 A generation silent past the stated idle bound after its first frame ends in band with SPEC 6's committed-stream shape: one error frame plus `[DONE]`, no new truncation format
- [ ] #4 The bound and its code name come from TASK-34's adopted values; the behaviour is pinned by `RequestPhaseTimeoutTests` and named in the SPEC 14.1 row
- [ ] #5 SPEC 14.1 row 2 no longer lists this as open, and a test pins each of 1-3 without a real engine
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
For a positive byte budget, count queued bytes and bytes awaiting flush. Pause upstream reads when credit runs out and resume on returned credit. Abort a pending write after 30 seconds without progress. Independently enable the per-engine idle timer for a pending upstream read after the first raw byte. Exclude time spent waiting for downstream credit. Transfer only validated event bytes and commit successful EOF after the final flush. Retain each stream’s selected configuration across reload and preserve native physical cleanup, opt-out and library behavior.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Independent review approved the opt-in scope. Delivery found fixture failures on 8c5c15e. The implementer reported 17/17 on 7c18160. Acknowledgment and termination-cause ordering require corrections and independent qualification. Positive defaults still require representative gap replay and a stopped-reader soak.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
<!-- COMMENTS:END -->
