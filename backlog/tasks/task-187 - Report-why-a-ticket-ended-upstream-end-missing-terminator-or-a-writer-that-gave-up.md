---
id: TASK-187
title: >-
  Report why a ticket ended: upstream end, missing terminator, or a writer that
  gave up
status: Ready for Review
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-25T10:49:00Z'
updated_date: '2026-11-27'
labels: []
dependencies:
  - TASK-189
priority: high
ordinal: 179000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
A stream that ends with no finish_reason and no [DONE] sentinel currently ends as a success, and the extension splices the next turn's tokens onto the truncated text. Slice 1 is merged: the truncation is reported with its end reason, its bytes are not replayed into the prompt, and the truncated prefix is captured once. Slice 2 names how each upstream ended on the finished ticket, and covers the capacity-warning case the owner saw, where one warning killed other agents' streams.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A finished ticket states how its upstream ended: complete, ended without a terminator, or failed, and the event feed carries it.
- [ ] #2 A downstream stream that stopped early records the reason and does not read as completed.
- [ ] #3 The existing wire bytes do not change: an in-band error frame and terminator still end a failed stream.
- [ ] #4 One ticket produces exactly one terminal event and one terminal state, including the in-band error plus terminator path that today can settle as completed.
- [ ] #5 Cancellation stays its own axis: a client hang-up or an induced abort is never reclassified as an upstream end reason.
- [ ] #6 An additive authoritative protocol outcome distinguishes complete, incomplete, failed and unknown; clean EOF or legacy Completed alone cannot establish successful generation or complete usage.
- [ ] #7 One bounded semantic observation contract supplies first raw byte, first semantic output, model identity, usage availability and authoritative outcome; local Tune uses fixture/local observations, commercial extends it.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Extend existing SSE framing with one bounded UTF-8 semantic observer; remove the separate raw terminator scan. Preserve frame bytes, writer acknowledgment and cancellation axis. Freeze one shared record with monotonic raw/semantic timing, bounded model identity, usage availability and authoritative complete/incomplete/failed/unknown outcome. Expose additive terminal ticket/event fields.

Precedence: structured error, transport/idle failure, committed terminator, then clean finish evidence for every requested/observed choice; otherwise incomplete. Malformed/over-limit evidence cannot prove success. Cap staged payload, JSON depth, choices and identity; retain no output history. Publish at the existing terminal winner and carry upstream failure through opt-in final acknowledgment without appending duplicate frames. Pin byte identity, error-plus-terminator, split delimiters, EOF/torn frames, missing/zero metrics, sibling isolation and writer/cancellation races. Update SPEC §§6/10/14 and README. Nonstreaming, writer accounting, normalization and retries remain separately owned.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Candidate 56b24d3cbb21e2554454cc8931f20d03dc175488 against main 62f1c79 implements AC1–6 and AC7's shared observation foundation; downstream Tune/commercial consumption remains separately owned. Independent plan review passed before code. Terminal CAS freezes the bounded synchronized record; malformed/over-limit data disables positive completion, and invalid usage remains sticky-invalid. Preserve PR #83 progress fields and PR #86 parsing ownership.

Independent source review of 934e790..56b24d3 passed after fixing lost structured-error evidence when a later field in the same event is malformed/overdeep. Added complete-versus-partial error, byte-preservation and partial-positive-evidence regressions. Release verification at exact 56b24d3: 150 passed, zero failed/skipped across SemanticStreamObservationTests, StreamFrameIdentityTests, StreamRelayTests, StreamingFailureTests and EventSurfaceTests; build/analyzers passed. SPEC v0.12.164 records behavior and limitations.

Historical attempts and accepted wire disposition remain at the [planning baseline](https://github.com/ArborealML/Inference-Router/commit/62f1c79de7b25486cf8669de654a2fd57d8993a2). Boundary EOF may retain legacy Completed with authoritative incomplete; cancellation is orthogonal and upstream error adds no second terminal frame. No nonstreaming, commercial, hardware or installed-client qualification is claimed. Coordinator next publishes the reviewed candidate through an attribution-compliant capability, then checks exact-head hosted build/test/AOT. Backlog CLI unavailable; controlled Markdown and strict board validation substitute. Not Done before integration and remaining qualification.
<!-- SECTION:NOTES:END -->
