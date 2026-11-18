---
id: TASK-108
title: Send stream keepalives in a frame a strict OpenAI client can ignore
status: To Do
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-18T17:43:00Z'
updated_date: '2026-11-18T19:54:00Z'
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
- [x] #1 A named pin records what a strict OpenAI-shaped client parser does with today's keepalive frame; the answer is measured, not assumed
- [x] #2 Keepalives arrive in a frame a conforming SSE parser ignores without error, and SPEC §6 states the shape in one line
- [x] #3 The frame keeps its seq and status, and TASK-91's monotonic-seq pins keep their meaning after the change
- [x] #4 Byte-level tests prove nothing else moved: upstream bytes identical, one terminal representation, `[DONE]` unchanged
- [x] #5 A live-router proof, not only a harness: one streamed completion with the safe-smoke posture, no lifecycle commands
- [ ] #6 CLI process tests cover text and JSON output, progress comments, upstream errors and early EOF. A stream ending before its completion sentinel exits nonzero.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Detect a missing completion sentinel in streaming chat and complete. Preserve partial stdout, send a diagnostic to stderr, and return a failing exit code. Exercise the actual CLI in human and JSON modes against bounded loopback fixtures for successful completion, upstream failure, progress comments, and EOF with or without a preceding delta. Preserve nonstream behavior and document exit semantics in SPEC.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
At [4a5e214](https://github.com/ArborealML/Inference-Router/commit/4a5e214) the strict client rejected the old data payload and accepted the new comment writer. The standalone pi smoke observed seq 2 before admission, a completed turn and 379 preserved upstream bytes. The filtered stream set passed 36 checks. A separate CLI process printed partial and returned exit 0 on orderly EOF after one delta. The independent plan review confirmed the sentinel contract and existing chat/complete alias. Use focused C# subprocess fixtures for stdout, stderr, exit status, comments, error-plus-sentinel and EOF. Keep prior upstream error reporting singular, preserve JSON bytes and human trailing newline, and bound child cleanup. TASK-82 owns mid-frame failures, TASK-81 terminal arbitration and TASK-91 historical sequence reproduction.
<!-- SECTION:NOTES:END -->
