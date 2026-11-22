---
id: TASK-170
title: Run a repeatable live tuning workload for one engine and model
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-22T09:21:00Z'
labels: []
milestone: m-14
dependencies:
  - TASK-149
  - TASK-146
references:
  - docs/hardware-tuning-design.md
ordinal: 165000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Implement air tune run for one configured engine, model and profile. Reuse the offline planner and comparison format. Use Hy4 for the first live reference run.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Resolve aliases and verify actual backend model, context and slots before accepting samples. Record hardware, runtime, effective launch settings and workload identity.
- [ ] #2 Run a repeatable short workload and a representative long prompt. Record cold-load time, time to first token, prefill tokens/sec and decode tokens/sec separately, with token counts and cache conditions.
- [ ] #3 Persist successes and failures. Two compatible saved runs can be compared with air tune compare, with long-prompt measurements retained separately from the short grid.
- [ ] #4 Verify the runner against native Hy4 measurements on an idle host and preserve active user requests.
<!-- AC:END -->
