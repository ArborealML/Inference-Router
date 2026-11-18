---
id: TASK-82
title: >-
  Prove the stream relay preserves every frame when the upstream splits it at
  any byte
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T04:09:00Z'
updated_date: '2026-11-18T04:13:00Z'
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
The fixture set covers a multi-byte UTF-8 character split across chunks, a frame split inside the `data:` prefix, an empty `data:` frame, a comment-only keepalive frame, `[DONE]`, and an unknown field inside a chunk
A truncation and an upstream disconnect mid-frame produce the terminal error body in the single existing lowercase shape and a closed stream, never a silently shortened answer
No test sleeps: boundaries are driven by the fixture, and the whole set runs against the same code path the release binary uses
The fixture set covers a multi-byte UTF-8 character split across chunks, a frame split inside the `data:` prefix, an empty `data:` frame, a comment-only keepalive frame, `[DONE]`, and an unknown field inside a chunk.
A truncation and an upstream disconnect mid-frame produce the terminal error body in the single existing lowercase shape and a closed stream, never a silently shortened answer.
No test sleeps: boundaries are driven by the fixture, and the whole set runs against the same code path the release binary uses.
<!-- AC:END -->
