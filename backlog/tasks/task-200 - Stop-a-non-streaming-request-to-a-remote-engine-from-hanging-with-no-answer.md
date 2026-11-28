---
id: TASK-200
title: Stop a non-streaming request to a remote engine from hanging with no answer
status: To Do
assignee:
  - '@gpt-6.2-sol-release'
created_date: '2026-11-26T11:53:00Z'
updated_date: '2026-11-28T11:08:00Z'
labels:
  - wire
  - lifecycle
milestone: m-12
dependencies: []
priority: high
ordinal: 192000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
A chat completion asked without the stream flag against the newly added remote provider never returns: the client gets no bytes and no refusal, and the router does not answer inside its own response timeout. The same model works when streamed, and a non-streaming request against a locally hosted engine completes in seconds. So the buffered path through a remote upstream is broken, and the caller waits forever rather than learning anything went wrong. This is user-visible the moment a remote model is selected from Pi, because the extension's ordinary turns are streamed but its auxiliary calls are not always.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A non-streaming completion request to a configured remote OpenAI-compatible engine returns its body, inside the configured response timeout, from a fake upstream that answers once and keeps the connection open.
- [ ] #2 The handoff names the await that never completed, by file and line, and what about the remote response shape made it hang.
- [ ] #3 A fake upstream that accepts the request and then sends nothing settles with the existing named timeout reason instead of hanging, and the refusal shape on the wire is unchanged.
- [ ] #4 Streaming requests to the same engine are unaffected: the existing stream pins stay green.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Measured on the live host: a non-stream request to the remote provider got zero bytes and was cut by the client at 35s; a streamed request to the same model returned tokens; a non-stream request to the local engine returned 712 bytes in 9s. Queue depth stayed empty and the engine reported healthy with nothing in flight, so the request is inside the forward path, not queued.
<!-- SECTION:NOTES:END -->
