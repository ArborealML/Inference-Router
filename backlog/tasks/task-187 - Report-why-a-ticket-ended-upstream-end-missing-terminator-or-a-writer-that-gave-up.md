---
id: TASK-187
title: >-
  Report why a ticket ended: upstream end, missing terminator, or a writer that
  gave up
status: In Progress
assignee:
  - '@gpt-6.2-sol-shutdown'
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
Implementing against main 62f1c79 after independent plan PASS with bounded conditions. Malformed/over-limit data disables positive completion even after DONE; stronger failures remain authoritative. Usage requires unambiguous nonnegative prompt/completion counts and checked optional total; invalid reports remain sticky-invalid. Terminal CAS freezes a synchronized snapshot; late work cannot rewrite it. Protocol work owns forwarding/completion, observation DTOs and terminal projection; TASK-203 owns dispatch/queue ranges. Preserve PR #83 progress fields and PR #86 parsing ownership.

Historical failed attempts and accepted wire disposition remain at [planning baseline](https://github.com/ArborealML/Inference-Router/commit/62f1c79de7b25486cf8669de654a2fd57d8993a2). Boundary EOF without a terminator may retain legacy Completed while authoritative outcome is incomplete. Frozen end names: complete, upstream_closed_without_terminator, stream_incomplete, failed, unknown; cancellation stays orthogonal. No second terminal frame after upstream error. TASK-170/210 consume the shared contract, without implying tuning/commercial qualification. Local SDK/Backlog CLI unavailable; strict board validation and hosted exact-head build/test/AOT remain required.
<!-- SECTION:NOTES:END -->
