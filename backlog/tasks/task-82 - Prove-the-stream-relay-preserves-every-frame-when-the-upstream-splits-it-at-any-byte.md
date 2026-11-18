---
id: TASK-82
title: >-
  Prove the stream relay preserves every frame when the upstream splits it at
  any byte
status: Waiting
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T04:09:00Z'
updated_date: '2026-11-18T10:20:00Z'
labels:
  - stream
  - wire
  - tests
milestone: v0.2 — the switching backend
dependencies: []
ordinal: 82000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The relay copies upstream bytes to the client as they arrive, so a frame can be split at **any** byte and the client must still see one identical event stream. Nothing pins that today: the streaming tests assert that keepalives and frames arrive, not that a frame split mid-token or mid-UTF-8-sequence survives the hop. The external evaluation of an open-source AI gateway (2026-11-18, `katanemo/plano`, borrowed pattern) pointed straight at this gap: their SSE tests replay one fixture across shifted 64-byte boundaries and assert identity, no loss and no duplication. That is the shape to copy — cheap, no new runtime, and it guards the one promise the router makes on `/v1` streaming: pass the answer through.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 One SSE fixture is replayed through the relay at every byte-offset split of a short fixture, plus 64-byte-aligned shifts of a long one, and the client's received bytes are identical to the upstream's in every case
- [ ] #2 The fixture set covers a multi-byte UTF-8 character split across chunks, a frame split inside the `data:` prefix, an empty `data:` frame, a comment-only keepalive frame, `[DONE]`, and an unknown field inside a chunk
- [ ] #3 A truncation and an upstream disconnect mid-frame produce the terminal error body in the single existing lowercase shape and a closed stream, never a silently shortened answer
- [ ] #4 No test sleeps: boundaries are driven by the fixture, and the whole set runs against the same code path the release binary uses
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
A delegated worker delivered three tests at 07:45Z, uncommitted in its own worktree: a 99-byte fixture split at all 100 offsets, a 27,636-byte fixture at 64 alignments, and a queued keepalive before the first frame. Its evidence — a 0-warning build, ten direct runs, one full suite — is its report. The coding seat re-measures and commits it under its own identity (decision-43), and the criterion stays unchecked until the tests are on `main`. Wake: the coding seat's review of that diff — re-measured, then committed under its own identity — or a decision to rebuild the tests from the plan instead. Expect collisions: the worker changed the shared wire harness, and `Engine(...)` now takes a `models:` parameter.

**Correction, and a retraction.** The note this seat replaced here cited a checker rule (`contract_stability` in `tools/board-check.py`) forbidding new pins in `StreamRelayTests`/`Backpressure`/`RequestPhaseTimeout`/`StreamCancellation`. That rule does not exist: `grep -n contract_stability|StreamRelayTests tools/board-check.py` is empty, and no such file is in the test project. The only standing rule of that kind is on TASK-87 and names the switch-claim and capability-publication classes. Nothing here is frozen by filename.
The top-tier plan review came back REVISE; its findings hold against this tree: the relay pumps raw byte chunks into an unbounded channel (`Channel.CreateUnbounded`, `RouterCore.cs:420` and `:566`), so a partial upstream frame can merge with a later error frame — publish complete events and discard the incomplete tail. A blocked reader therefore has no bound today. Sweep split and death offsets in memory at every offset of small sample events, plus one real-HTTP bytewise smoke, rather than choosing a few. The inter-frame stall is not measured by `response_timeout_ms`, which is armed once before headers: that belongs to TASK-30's idle bound, not here. Do not build a second HTTP harness; reuse the existing one.
SECTION:NOTES:END -->
