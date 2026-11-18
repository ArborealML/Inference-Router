---
id: TASK-79
title: >-
  Decision: may a caller name its submission so a retry cannot run the same
  inference twice
status: Blocked
assignee:
  - '@claire'
created_date: '2026-11-18T02:59:00Z'
updated_date: '2026-11-18T03:01:00Z'
labels:
  - wire
dependencies:
  - TASK-73
priority: medium
ordinal: 79000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Claire's call.** A forwarder that waits and retries, or any client that reconnects mid-request, can make the router run the same inference twice: nothing identifies a submission across connections, and nothing dedups. Honouring a caller-supplied submission name changes the `/v1` surface, which the frozen wire contract reserves to you. Options: **(a)** accept a caller-supplied key on `/v1` with a bounded dedup window, so a retry in the window gets the first attempt's outcome; **(b)** leave `/v1` untouched and make the local forwarder remember what it already forwarded, refusing to re-send — fixes the case the forwarder creates, needs persistence if it restarts; **(c)** accept the duplicate and document it. My recommendation: **(b)** now, and revisit (a) only when a caller outside your control needs it. Answer needed in one line, plus whether the dedup window has to survive a router restart. Unblocks the forwarder's retry rule and the fleet queue's admission.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 One line: (a) a caller-supplied key honoured on /v1, (b) dedup inside the local forwarder only, or (c) documented duplicate
- [ ] #2 Say whether the dedup state has to survive a router restart, which decides whether it lives in memory or on disk
<!-- AC:END -->
