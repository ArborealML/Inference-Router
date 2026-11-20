---
id: TASK-82
title: >-
  Prove the stream relay preserves every frame when the upstream splits it at
  any byte
status: Done
assignee:
  - '@gpt-6.2-sol-stream-delivery'
created_date: '2026-11-18T04:09:00Z'
updated_date: '2026-11-20T02:48:00Z'
labels:
  - stream
  - wire
  - tests
milestone: m-1
dependencies: []
priority: high
ordinal: 82000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The completion relay preserves complete upstream events across byte splits. An incomplete event at EOF, cancellation or a read fault cannot become a silently shortened answer.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 One SSE fixture is replayed through the relay at every byte-offset split of a short fixture, plus 64-byte-aligned shifts of a long one, and the client's received bytes are identical to the upstream's in every case
- [x] #2 The fixture set covers a multi-byte UTF-8 character split across chunks, a frame split inside the `data:` prefix, an empty `data:` frame, a comment-only keepalive frame, `[DONE]`, and an unknown field inside a chunk
- [x] #3 A truncation and an upstream disconnect mid-frame produce the terminal error body in the single existing lowercase shape and a closed stream, never a silently shortened answer
- [x] #4 No test sleeps: boundaries are driven by the fixture, and the whole set runs against the same code path the release binary uses
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Stage one incomplete SSE event with a 1 MiB pre-delimiter limit. Forward complete blocks with exact delimiters, including split CRLF. Handle incomplete EOF and oversized blocks with the existing terminal error shape and preserve read fault mapping. End HTTP comments on the first complete block while retaining first raw byte timing for the upstream deadline and metric. Pin EOF, fault, size and heartbeat cadence with focused fixtures and document client behavior.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The split-offset fixture landed at 27f7380. The pre-fix EOF and read-fault tests reproduced both wire failures. [PR #42](https://github.com/ArborealML/Inference-Router/pull/42) landed as 9e67ff1 with SPEC v0.12.100. The corrected head passed 57 focused Release tests and server Native AOT with zero trim warnings. Its tested stream code, fixtures and docs match the merge. [Merge CI](https://github.com/ArborealML/Inference-Router/actions/runs/44662760802) passed.
<!-- SECTION:NOTES:END -->
