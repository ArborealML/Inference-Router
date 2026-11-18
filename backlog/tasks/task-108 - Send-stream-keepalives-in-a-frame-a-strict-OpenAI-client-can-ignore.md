---
id: TASK-108
title: Send stream keepalives in a frame a strict OpenAI client can ignore
status: Waiting
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-18T17:43:00Z'
updated_date: '2026-11-18T19:45:00Z'
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
The strict parser rejected the old data payload and accepted a completion with the comment writer in [4a5e214](https://github.com/ArborealML/Inference-Router/commit/4a5e214). Queued wire tests observe seq 0, 1 and 2, escaped status, byte-identical upstream frames and the terminal sentinel. A lifecycle-free standalone router streamed a real pi turn after periodic seq 2; 379 upstream bytes were preserved. The abort control observed TCP close, ticket cancellation, slot release and a subsequent completion. The implementation passed 36 focused checks and a zero-warning Linux AOT publish. A full Debug run exposed the independent nudge failure, reproduced on the parent (TASK-80). The commit review verified framing and identified the CLI EOF exit defect. Four manual CLI mode checks passed, but process-level regression coverage and early EOF handling remain. TASK-82 owns mid-frame failures and TASK-81 owns terminal arbitration. TASK-91 still owns reproduction of its historical sequence repeat.

A separate CLI process posted to a loopback HTTP server that returned one valid delta and clean EOF. It printed partial, wrote no error and exited 0. Wake when the external CLI completion and regression plan review returns.
<!-- SECTION:NOTES:END -->
