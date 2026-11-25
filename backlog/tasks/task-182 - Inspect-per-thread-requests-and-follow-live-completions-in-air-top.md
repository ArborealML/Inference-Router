---
id: TASK-182
title: Inspect per-thread requests and follow live completions in air top
status: To Do
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-25T03:55:00Z'
labels: []
milestone: m-15
dependencies: []
ordinal: 175000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add a Threads tab to the terminal monitor. An operator selects a thread, browses recent requests, inspects the actual model input for a selected request, and watches its completion arrive live. Capture at AIR so all clients are covered. Store request-time snapshots independently of later thread edits.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 List threads by recent activity with title and origin when supplied. Within each thread show the last configurable N requests in admission order, including overlapping requests, their model, profile, timestamps and outcome.
- [ ] #2 Selecting a request shows its captured messages, tools and generation settings. Mark uncaptured, truncated and evicted content explicitly. Distinguish request-time input from generated output.
- [ ] #3 Opening a running request shows output already received and then streams subsequent text and tool-call deltas in order. Closing the view leaves inference running. Completed, cancelled and failed requests retain their available output and terminal state.
- [ ] #4 Bound capture by count and bytes across threads. Restrict content inspection to authorized operators, exclude transport credentials, and keep raw prompts and completions out of the general event feed.
- [ ] #5 A slow or disconnected viewer cannot stall inference. Reconnecting resumes at a cursor or identifies a gap. Interleaved live streams stay attributed to the correct request.
- [ ] #6 A loopback test holds inference open after the first delta and verifies that the monitor shows it before completion. Verify late attachment, terminal history, concurrent requests, and an evicted slot.
<!-- AC:END -->
