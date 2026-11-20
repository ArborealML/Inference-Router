---
id: TASK-99
title: >-
  Cap the caller-facing text in keepalive frames and in an error frame on a
  committed stream
status: In Progress
assignee:
  - '@gpt-6.2-sol-stream-frames'
created_date: '2026-11-18T11:14:00Z'
updated_date: '2026-11-20T17:16:00Z'
labels: []
milestone: m-1
dependencies: []
ordinal: 98000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Native confirmed the stream and lifecycle source edits are independent. Frames has the implementation release for the reviewed keepalive identifier projection and wire-size fixtures. The upstream error-body read ceiling landed separately in [PR #44](https://github.com/ArborealML/Inference-Router/pull/44).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Keepalive control frames carry the identifier at its reporting length, and a named pin asserts the frame length
- [ ] #2 Each HTTP ceiling is asserted against a byte ceiling named in SPEC, and a long error frame on a committed stream has its own pin
- [ ] #3 Removing only the queued-model display cap fails the named queued keepalive regression; restoring it passes.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Apply WireText.Ident to displayed model and engine identifiers before comparing emitted keepalive status. Preserve cadence, delimiters and routing identity. Specify encoded byte ceilings of 1,727 for a keepalive, 9,856 for plain error JSON and 9,878 for a committed error plus DONE. Exercise all status arms and the long committed error on the wire.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The queued display was the demonstrated unbounded path. The admitted model and the existing terminal reason cap stay intact. Removing the queued display cap replaces the obsolete admission mutation.
<!-- SECTION:NOTES:END -->
