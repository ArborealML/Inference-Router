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
Delivered 07:45Z by a delegated worker, uncommitted in its own worktree: three tests — a 99-byte fixture split at all 100 byte positions, a 27,636-byte fixture at 64 alignments, and a queued keepalive before the first upstream frame. Its reported evidence, which the reviewer re-measures rather than repeats: a build with 0 warnings, ten consecutive direct runs of the built test assembly (30 passes), and three deliberate relay mutations each failing the matching pin — decoding and re-encoding the raw byte copy, dropping the last byte of each chunk, and inserting a keepalive mid-frame. It observed no corruption in the unmodified relay and showed that every keepalive write precedes the channel drain, which is the fact TASK-91's defect has to be reconciled against.

Wake condition: the implementer seat reviews that diff, re-runs the build and that filter, and commits it under its own identity (decision-43); the criterion stays unchecked until the tests are on `main`. Two collisions to expect: the worker changed the shared wire harness, and main has moved — `Engine(...)` in the switch-claim class now takes a `models:` parameter and the fake `/models` handler reports loaded models through it, so rebase on `d6cfb38` instead of re-deriving the fixture.
Ruling on the proposed plan: the frozen-file rule still forbids **new** pins or waits in `StreamRelayTests`/`Backpressure`/`RequestPhaseTimeout`/`StreamCancellation`, and renaming tests to dodge it dodges a rule that exists because this class flaked. Amending an existing assertion is allowed: `StreamRelayTests.cs:79` already asserts a `truncated` frame is absent, and SPEC v0.12.24 already made that sentinel a wire-contract change. New evidence in a new file is fine. The sol-pro plan review owns the open questions: is a stall *between frames* measurable while `response_timeout_ms` is armed once before headers, and what signals truncation once the 200 is committed? TASK-30 owns the stalled half.
<!-- SECTION:NOTES:END -->
