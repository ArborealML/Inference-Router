---
id: TASK-82
title: >-
  Prove the stream relay preserves every frame when the upstream splits it at
  any byte
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T04:09:00Z'
updated_date: '2026-11-18T19:18:00Z'
labels:
  - stream
  - wire
  - tests
milestone: m-2
dependencies: []
ordinal: 82000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The relay copies upstream bytes to the client as they arrive, so a frame can be split at **any** byte and the client must still see one identical event stream. Nothing pins that today: the streaming tests assert that keepalives and frames arrive, not that a frame split mid-token or mid-UTF-8-sequence survives the hop. The external evaluation of an open-source AI gateway (2026-11-18, `katanemo/plano`, borrowed pattern) pointed straight at this gap: their SSE tests replay one fixture across shifted 64-byte boundaries and assert identity, no loss and no duplication. That is the shape to copy — cheap, no new runtime, and it guards the one promise the router makes on `/v1` streaming: pass the answer through.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 One SSE fixture is replayed through the relay at every byte-offset split of a short fixture, plus 64-byte-aligned shifts of a long one, and the client's received bytes are identical to the upstream's in every case
- [ ] #2 The fixture set covers a multi-byte UTF-8 character split across chunks, a frame split inside the `data:` prefix, an empty `data:` frame, a comment-only keepalive frame, `[DONE]`, and an unknown field inside a chunk
- [ ] #3 A truncation and an upstream disconnect mid-frame produce the terminal error body in the single existing lowercase shape and a closed stream, never a silently shortened answer
- [x] #4 No test sleeps: boundaries are driven by the fixture, and the whole set runs against the same code path the release binary uses
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
AC #1 landed at 27f7380 (`StreamFrameIdentityTests`, 205 lines, plus four lines in the shared wire harness). The coding seat's report: 0-warning build, 3/3 on three consecutive focused runs, full suite 215/0, and two of its own mutation controls — drop the last byte of every copied chunk, insert a keepalive after every chunk — reddening all three pins and restoring byte-identical. This seat's own check: CI 44294384525 green at that sha. Still open: #2 fixture coverage (no empty `data:` frame) and #3 truncation and mid-frame disconnect — the existing relay test only asserts an absent `truncated` frame. On #4 this seat grepped the new file for sleeps and found none; the "same code path the binary uses" half is the coding seat's report. The top-tier plan review came back REVISE and its findings hold: the relay pumps raw chunks into an unbounded channel (`RouterCore.cs:420`, `:566`), so a partial frame can merge with a later error frame — publish complete events, discard the tail; sweep split and death offsets in memory plus one bytewise HTTP smoke; the inter-frame stall is TASK-30's idle bound, not `response_timeout_ms`. Retracted: an earlier note here cited a `contract_stability` checker rule freezing four stream test classes; no such rule or file exists. Wake: the follow-up commit for #2 and #3.

Review of 4a5e214 confirms AC #3 remains open: the writer forwards an incomplete event before a read fault and can append the error into that event. Pin failure mid-line and between a complete data line and its blank terminator. Reconcile bounded retention with the existing arbitrary-split streaming guarantees before changing the relay.
<!-- SECTION:NOTES:END -->
