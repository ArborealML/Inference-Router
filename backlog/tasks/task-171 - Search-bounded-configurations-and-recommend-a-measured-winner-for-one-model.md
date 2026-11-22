---
id: TASK-171
title: Search bounded configurations and recommend a measured winner for one model
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-22T09:21:00Z'
labels: []
milestone: m-14
dependencies:
  - TASK-170
references:
  - docs/hardware-tuning-design.md
ordinal: 166000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Use the single-model runner to compare declared profiles and bounded engine-supported settings. Use MiniMax long-prompt prefill as the first tuning experiment after the reference runner is qualified.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Define the workload, scoring metric, context and concurrency requirements before running. The initial Pi case minimizes long-prompt time to first token at 262144 context and one active request.
- [ ] #2 Vary one supported setting at a time, verify effective values, and retain launch and host evidence. Include context, concurrency and engine-specific cache or prefill calibration settings where verifiable.
- [ ] #3 For the MiniMax experiment record disk reads, major page faults, memory and GPU utilization alongside throughput. A smaller-context trial remains separately identifiable when it misses the required context.
- [ ] #4 Report the best verified candidate and runner-up when available, measured tradeoffs, failed and untested candidates, or no qualifying configuration. Applying a recommendation is a separate explicit action.
<!-- AC:END -->
