---
id: TASK-58
title: Bound the per-model queue map so unseen model names do not accumulate state
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T20:41:00Z'
labels:
  - scheduling
dependencies: []
priority: medium
ordinal: 58000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Router state grows with every model name anyone has ever asked for. RouterCore.cs:18 holds _queues as a plain Dictionary keyed by canonical model name with no global bound and no eviction; max_queue_per_model defaults to 64 (Config/RouterConfig.cs:141) and bounds the depth of one queue, not the number of queues. So a client that probes a stream of distinct names — a misconfigured caller, a scanner, a fuzz run — leaves one live queue object per name for the process's lifetime even though each is empty, and the live view enumerates all of them. This is the queue-shaped half of the admission story: refusing an unservable model at admission (20a06fa) stopped tickets accumulating, and nothing here stops queue shells accumulating. Either a ceiling on distinct queues with a stated refusal, or an eviction rule for empty queues, with the race against a concurrent arrival resolved explicitly. Adjacent to TASK-51 and TASK-54; neither has to land first.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 SPEC 5.5 states the bound or eviction rule for distinct model queues, with the default named
- [ ] #2 a test shows N distinct model names cost a bounded amount of router state
- [ ] #3 the rule cannot evict or refuse a queue that holds a live ticket
<!-- AC:END -->
