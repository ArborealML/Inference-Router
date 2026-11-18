---
id: TASK-82
title: >-
  Prove the stream relay preserves every frame when the upstream splits it at
  any byte
status: Waiting
assignee:
  - '@codex-6.2-sol'
created_date: '2026-11-18T04:09:00Z'
updated_date: '2026-11-18T08:10:00Z'
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Delegated by the owner's 07:06Z instruction ("Codex should probably be able to help… start a Codex tmux session in the inference router project"). Running since 07:18Z as `gpt-6.2-sol` (medium reasoning) in an **isolated worktree** on branch `codex/task-82-frame-identity` — the implementer seat keeps sole write access to `main`, so nothing from that branch reaches `main` except through review. Brief: tests only; split the upstream at 0 bytes, 1 byte, mid-frame, mid-CRLF, exact boundary and across a keepalive fire; no sleeps or scheduling luck; do not edit `src/` to make a test pass — a defect found there is reported, not fixed, because another seat owns it; commit prefix `test:`, no push. Report lands with commands, counts and mutation evidence.

**Verify before merging:** that worktree shares `main`'s repository config (decision-42), so its commits carry whatever identity the shared config holds. Check author and committer, and correct on the branch — unpushed, therefore amendable — rather than letting a human identity through or rewriting anything published.

Worker finished at 07:45Z with three tests, uncommitted in its own worktree: build passed with 0 warnings, the built test assembly was run directly ten consecutive times (30 passes), and three separate mutations of the relay each failed the matching pin — so the pins discriminate rather than decorate. It observed no frame corruption, and showed that every keepalive write precedes the channel drain. Its sandbox could not write the shared gitdir, so the commit is a seated reviewer's act, not the worker's. Wake condition: the implementer seat reviews that diff and commits it; the criterion stays unchecked until the tests are on main.
<!-- SECTION:NOTES:END -->
