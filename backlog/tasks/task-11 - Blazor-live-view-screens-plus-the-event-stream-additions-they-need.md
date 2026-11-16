---
id: TASK-11
title: 'Blazor live view: screens plus the event-stream additions they need'
status: To Do
assignee: []
created_date: '2026-11-16T20:46:00Z'
updated_date: '2026-11-16T20:46:00Z'
labels:
  - ui
milestone: v0.10 - live view and robustness parity
dependencies:
  - TASK-9
ordinal: 11000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Build the Blazor live view on the event stream, taking the good parts of PAIR's UI (workload
rail, filters, topology, endpoint copy, actionable errors) and dropping the appliance parts
(installer, model deletion, pairing, autostart/update/wipe). Our screens differ: global inference
activity, queues and admission, engines/profiles/switches, topology and trends, a request timeline,
and health/config/security. The event stream must carry the fields these screens need before any of
them can be built, so the stream work comes first.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Ticket records expose owner, state, priority, queue, attempt, active profile, required/active context, capacity, timing, result, prompt/completion bytes, tokens, prefix hash, config pin and commit boundary
- [ ] #2 Events cover request lifecycle, rejection, capacity change, queue, health/crash/restart, switch/profile, prefix and an explicit stream gap; delivery is append + counters only
- [ ] #3 `/admin/status` reports active/queued/readiness/rates; `/admin/events` supports filtering, heartbeat and bounded sequence-gap handling; a bounded `/admin/requests` exists
- [ ] #4 Admin JSON schema and casing pinned once (SPEC 15.1) and covered by contract tests
- [ ] #5 Screens render from the event stream alone: global inference, queues/admission, engines/profiles/switches, topology/trends, request timeline, health/config/security
<!-- AC:END -->
