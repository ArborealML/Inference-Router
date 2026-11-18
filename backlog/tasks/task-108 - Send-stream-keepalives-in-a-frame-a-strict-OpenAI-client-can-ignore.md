---
id: TASK-108
title: Send stream keepalives in a frame a strict OpenAI client can ignore
status: In Progress
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-18T17:43:00Z'
updated_date: '2026-11-18T18:34:00Z'
labels:
  - wire
  - stream
  - serve
dependencies: []
references:
  - src/Arboreal.InferenceRouter.Server/ServerApp.cs
modified_files:
  - tests/Arboreal.InferenceRouter.Tests/StreamFrameIdentityTests.cs
  - src/Arboreal.InferenceRouter.Server/AirServerJson.cs
  - src/Arboreal.InferenceRouter.Server/ServerApp.cs
priority: high
ordinal: 106000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The implementer is changing the stream writer and CLI under the reviewed plan. The external review and fixed legacy-parser control are complete.
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
Write a strict SSE regression with an actual completion chunk and a fixed legacy frame. Emit a reserved comment with invariant sequence, optional JSON-quoted status and a blank line. Validate escaping after encoding. Human CLI mode recognizes exact progress comments and prints status changes once. JSON mode drops comments and preserves data and error frames. Handle streamed errors with a failing exit code. Preserve arbitrary upstream bytes; assert success with a single upstream sentinel and a pre-sentinel failure with one error and sentinel. TASK-81 owns post-sentinel failure and terminal races. Use a gated loopback upstream to prove downstream abort closes the upstream connection, releases capacity and permits a subsequent request. Exercise a real pi turn that queues past a periodic comment. Record the compatibility change in SPEC and the board decision.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
External plan review of the stream writer, CLI and tests at 3c83ecc returned on 2026-11-18. Verified the writer interpolates quoted status and the CLI reads choices unconditionally. Adopt exact grammar, strict-parser controls, socket-level abort proof and a queued real-client run. TASK-99 owns the final frame-size policy. TASK-81 owns universal terminal arbitration. TASK-91 retains its historical sequence reproduction. Use the native AOT serialization context.
<!-- SECTION:NOTES:END -->
