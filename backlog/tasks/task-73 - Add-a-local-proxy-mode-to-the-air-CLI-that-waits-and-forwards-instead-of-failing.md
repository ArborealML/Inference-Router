---
id: TASK-73
title: >-
  Add a local proxy mode to the air CLI that waits and forwards instead of
  failing
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T01:22:00Z'
updated_date: '2026-11-18T01:24:00Z'
labels: []
dependencies:
  - TASK-69
priority: medium
ordinal: 73000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The `air` CLI gains a proxy mode: a local server that receives client requests and forwards them to the real router, so a local agent talks to something that is always there and waits out a backend outage instead of failing. This is a new surface with its own lifecycle, timeouts and streaming behaviour, and it must never touch engine lifecycle commands.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 the proxy answers only by forwarding, and keeps the /v1 wire contract intact for success and for the one error shape
- [ ] #2 while the router is unreachable it waits and retries inside a named ceiling, then returns the router's own error shape
- [ ] #3 a stream that breaks mid-flight is reported honestly rather than truncated silently
- [ ] #4 the proxy never starts, stops or reconfigures a backend
<!-- AC:END -->
