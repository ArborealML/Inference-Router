---
id: TASK-108
title: Send stream keepalives in a frame a strict OpenAI client can ignore
status: Waiting
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-18T17:43:00Z'
updated_date: '2026-11-18T18:19:00Z'
labels:
  - wire
  - stream
  - serve
dependencies: []
references:
  - src/Arboreal.InferenceRouter.Server/ServerApp.cs
priority: high
ordinal: 106000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wake when the external wire-plan review returns. A queued OpenAI stream currently sends router progress as a data event, which a strict client tries to parse as a completion chunk. An SSE comment can carry progress without entering the client JSON decoder.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A named pin records what a strict OpenAI-shaped client parser does with today's keepalive frame; the answer is measured, not assumed
- [ ] #2 Keepalives arrive in a frame a conforming SSE parser ignores without error, and SPEC §6 states the shape in one line
- [ ] #3 The frame keeps its seq and status, and TASK-91's monotonic-seq pins keep their meaning after the change
- [ ] #4 Byte-level tests prove nothing else moved: upstream bytes identical, one terminal representation, `[DONE]` unchanged
- [ ] #5 A live-router proof, not only a harness: one streamed completion with the safe-smoke posture, no lifecycle commands
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Record the strict SSE parser failure on the original writer. Emit one SSE comment line per router keepalive, escaping embedded CR and LF while preserving sequence and status. Keep upstream bytes and terminal writes intact. Preserve CLI progress output and JSON mode. Pin queued sequence, success and failure termination, and actual upstream delivery on cancellation. Update the wire specification and consumer examples. Exercise an isolated lifecycle-free router with a gated loopback upstream and a real pi client. Submit the wire change for plan review before implementation.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Read at `2b751ec`: status interpolation `ServerApp.cs:244-245`, frame build `:251-252`, `Frame` emits `data:` at `AirServerJson.cs:50-53`. The alternative — keep the payload shape and document that clients must tolerate non-JSON frames — trades OpenAI compatibility for a frame a naive client can print; if that is chosen it is a posture the owner should see, and SPEC §6 plus the §6.1 client notes change with it. Either way a decision entry lands in the same push as the code (decision-54's pattern), and TASK-99's frame-length criterion moves with whichever shape wins.

The manager submitted the stream and CLI compatibility plan for external review on 2026-11-18. The implementer is preparing the strict-parser and abort evidence.
<!-- SECTION:NOTES:END -->
