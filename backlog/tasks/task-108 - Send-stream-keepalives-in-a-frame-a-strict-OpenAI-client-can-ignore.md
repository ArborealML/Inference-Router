---
id: TASK-108
title: Send stream keepalives in a frame a strict OpenAI client can ignore
status: To Do
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-18T17:43:00Z'
updated_date: '2026-11-18T19:18:00Z'
labels:
  - wire
  - stream
  - serve
milestone: m-0
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
Finish the CLI end-of-stream check and automated CLI regressions from the 4a5e214 review. The queued-client keepalive change is published.
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

The implementer reported a passing queued pi turn through the isolated router, byte-identical upstream chunks, upstream TCP close after client abort, and four CLI mode checks. The first full Debug run had one capability-publication failure (TASK-80).

Commit review dispatched for 4a5e214. The worker reproduced the sole full-suite failure on the unchanged parent (one failure in eleven focused attempts). Both changed-tree full suites passed 237/238, stream checks passed 36/36 and Linux AOT published without warnings. The coordinator verified 12/12 merged stop and stream tests.

Review verified framing, JSON quoting, upstream byte identity and AOT. The CLI currently returns success on EOF without a sentinel. Add a nonzero truncated-stream exit and process-level pins for text status, JSON, error and early EOF. TASK-82 #3 owns mid-frame failure and TASK-81 owns terminal arbitration.
<!-- SECTION:NOTES:END -->
