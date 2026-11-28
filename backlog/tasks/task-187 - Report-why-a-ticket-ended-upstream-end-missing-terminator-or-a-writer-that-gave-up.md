---
id: TASK-187
title: >-
  Report why a ticket ended: upstream end, missing terminator, or a writer that
  gave up
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-25T10:49:00Z'
updated_date: '2026-11-28T11:08:00Z'
labels: []
milestone: m-1
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
- [x] #1 A finished ticket states how its upstream ended: complete, ended without a terminator, or failed, and the event feed carries it.
- [x] #2 A downstream stream that stopped early records the reason and does not read as completed.
- [x] #3 The existing wire bytes do not change: an in-band error frame and terminator still end a failed stream.
- [x] #4 One ticket produces exactly one terminal event and one terminal state, including the in-band error plus terminator path that today can settle as completed.
- [x] #5 Cancellation stays its own axis: a client hang-up or an induced abort is never reclassified as an upstream end reason.
- [x] #6 An additive authoritative protocol outcome distinguishes complete, incomplete, failed and unknown; clean EOF or legacy Completed alone cannot establish successful generation or complete usage.
- [ ] #7 One bounded semantic observation contract supplies first raw byte, first semantic output, model identity, usage availability and authoritative outcome; local Tune uses fixture/local observations, commercial extends it.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Extend existing SSE framing with one bounded UTF-8 semantic observer; remove the separate raw terminator scan. Preserve frame bytes, writer acknowledgment and cancellation axis. Freeze one shared record with monotonic raw/semantic timing, bounded model identity, usage availability and authoritative complete/incomplete/failed/unknown outcome. Expose additive terminal ticket/event fields.

Precedence: structured error, transport/idle failure, committed terminator, then clean finish evidence for every requested/observed choice; otherwise incomplete. Malformed/over-limit evidence cannot prove success. Cap staged payload, JSON depth, choices and identity; retain no output history. Publish at the existing terminal winner and carry upstream failure through opt-in final acknowledgment without appending duplicate frames. Pin byte identity, error-plus-terminator, split delimiters, EOF/torn frames, missing/zero metrics, sibling isolation and writer/cancellation races. Update SPEC §§6/10/14 and README. Nonstreaming, writer accounting, normalization and retries remain separately owned.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Published [PR #89](https://github.com/ArborealML/Inference-Router/pull/89), head 7e42e0fca22565f61294b874de85338daca69250, implements AC1–6 and AC7's observation foundation. Tune/commercial consumption remains separately owned. [Source mappings](../milestones/wave1-publication-map.json) preserve reviewed 934e790..56b24d3 and the identical published tree under TASK-220's provenance exception.

Independent plan/source review passed after retaining fully observed structured errors when later same-event data is malformed/overdeep. Complete/partial-error, byte-preservation and positive-evidence regressions passed. Local Release at 56b24d3: 150 passed, zero failures/skips; build/analyzers passed. Terminal CAS freezes the bounded record; malformed/over-limit data disables positive completion and invalid usage remains sticky-invalid. SPEC v0.12.164 records scope.

[Hosted CI](https://github.com/ArborealML/Inference-Router/actions/runs/46112187952) passed on this actual head: 2,193 tests, zero failed/skipped, build, Linux AOT/config smoke, board and release harness. Synthetic merge/head trees match.

Preserve PR #83 progress and #86 parsing. Boundary EOF may retain legacy Completed with authoritative incomplete; cancellation remains orthogonal and upstream error adds no second terminator. Historical attempts remain at the planning baseline. Source landed in the [main integration](https://github.com/ArborealML/Inference-Router/commit/1bf85d3495ab2517a921d78013ffe69bbefb4325).
<!-- SECTION:NOTES:END -->
