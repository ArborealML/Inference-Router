---
id: TASK-108
title: Send stream keepalives in a frame a strict OpenAI client can ignore
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T17:43:00Z'
updated_date: '2026-11-18T17:44:00Z'
labels:
  - wire
  - stream
  - serve
dependencies: []
priority: high
ordinal: 106000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
A keepalive goes out as a `data:` payload, not an SSE comment: `ServerApp.cs:251-252` builds `[router keepalive seq=N …]` through `AirJson.Frame`, which emits `data: …\n\n` (`AirServerJson.cs:50-53`, read at `2b751ec`). SPEC §6 documents that shape honestly, and the doc comment at `ServerApp.cs:179` calls the same frames "comment frames" — one of the two is wrong. A strict OpenAI client parses every `data:` payload as a completion chunk, so a keepalive reads as a malformed answer: that sits between the router and pi consuming a queued or switching stream (TASK-89, TASK-85's streamed criterion). TASK-82's fixture list already asks for "a comment-only keepalive frame", a shape the router never emits.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A named pin records what a strict OpenAI-shaped client parser does with today's keepalive frame; the answer is measured, not assumed
- [ ] #2 Keepalives arrive in a frame a conforming SSE parser ignores without error, and SPEC §6 states the shape in one line
- [ ] #3 The frame keeps its seq and status, and TASK-91's monotonic-seq pins keep their meaning after the change
- [ ] #4 Byte-level tests prove nothing else moved: upstream bytes identical, one terminal representation, `[DONE]` unchanged
- [ ] #5 A live-router proof, not only a harness: one streamed completion with the safe-smoke posture, no lifecycle commands
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Read at `2b751ec`: status interpolation `ServerApp.cs:244-245`, frame build `:251-252`, `Frame` emits `data:` at `AirServerJson.cs:50-53`. The alternative — keep the payload shape and document that clients must tolerate non-JSON frames — trades OpenAI compatibility for a frame a naive client can print; if that is chosen it is a posture the owner should see, and SPEC §6 plus the §6.1 client notes change with it. Either way a decision entry lands in the same push as the code (decision-54's pattern), and TASK-99's frame-length criterion moves with whichever shape wins.
<!-- SECTION:NOTES:END -->
