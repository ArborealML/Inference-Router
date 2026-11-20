---
id: TASK-144
title: Plan hardware-specific qualification of stepped inference tiers
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-20T00:33:00Z'
updated_date: '2026-11-20T00:34:00Z'
labels: []
dependencies: []
ordinal: 142000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Design an operator-facing air tune command for one configured engine and model. Sweep supported context windows under a defined workload, measuring stable concurrency and speed on the target hardware. Retain comparable results across hardware upgrades. Preserve existing defaults and keep this planning work independent of the first native swap.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Propose a repeatable hardware-specific sweep recording model, runtime, GPU allocation, memory limits, requested and observed context, concurrency, and success or failure.
- [ ] #2 Define how measured results inform engine TOML and profile selection, including safe handling of failed or untested tiers and selection of normal defaults at 96k or higher where qualified, with smaller safe tiers explicit.
- [ ] #3 Define CLI inputs, a reproducible load near each target window, stability criteria, repetition and a search bound. Report highest demonstrated stable concurrency and distinguish measured failures from untested limits.
- [ ] #4 For each window, distinguish peak aggregate output tokens per second from single-stream decode speed. Record winning concurrency, prompt processing speed and time to first token with the workload and samples.
- [ ] #5 Save separate comparable runs with physical RAM, effective memory and swap limits, CPU, assigned GPU identities and VRAM, model and runtime identity, effective settings and workload. Demonstrate comparing 64 GB and 128 GB configurations.
<!-- AC:END -->
