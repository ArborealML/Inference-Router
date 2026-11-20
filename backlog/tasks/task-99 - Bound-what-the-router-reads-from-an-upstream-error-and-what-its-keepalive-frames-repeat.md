---
id: TASK-99
title: >-
  Cap the caller-facing text in keepalive frames and in an error frame on a
  committed stream
status: Done
assignee:
  - '@gpt-6.2-sol-stream-delivery'
created_date: '2026-11-18T11:14:00Z'
updated_date: '2026-11-20T17:57:00Z'
labels: []
milestone: m-1
dependencies: []
ordinal: 98000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The caller-facing keepalive and error frame limits landed in [PR #50](https://github.com/ArborealML/Inference-Router/pull/50), SPEC .107. All 56 focused Release tests passed and Native AOT had zero trim warnings. The baseline and queued-only mutation exceeded the 1,727-byte limit by one byte; restoring the projection passed all 56 again. The merged tree matches the reviewed integration and [post-merge CI](https://github.com/ArborealML/Inference-Router/actions/runs/44787290297) passed.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Keepalive control frames carry the identifier at its reporting length, and a named pin asserts the frame length
- [x] #2 Each HTTP ceiling is asserted against a byte ceiling named in SPEC, and a long error frame on a committed stream has its own pin
- [x] #3 Removing only the queued-model display cap fails the named queued keepalive regression; restoring it passes.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Apply WireText.Ident to displayed model and engine identifiers before comparing emitted keepalive status. Preserve cadence, delimiters and routing identity. Specify encoded byte ceilings of 1,727 for a keepalive, 9,856 for plain error JSON and 9,878 for a committed error plus DONE. Exercise all status arms and the long committed error on the wire.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The queued display was the demonstrated unbounded path. The admitted model and the existing terminal reason cap stay intact. Removing the queued display cap replaces the obsolete admission mutation.
<!-- SECTION:NOTES:END -->
