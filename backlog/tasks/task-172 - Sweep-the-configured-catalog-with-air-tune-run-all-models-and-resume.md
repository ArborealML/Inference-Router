---
id: TASK-172
title: Sweep the configured catalog with air tune run --all-models and --resume
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-22T09:21:00Z'
labels: []
milestone: m-14
dependencies:
  - TASK-171
references:
  - docs/hardware-tuning-design.md
ordinal: 167000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Run the same single-model search for every configured engine and canonical model, including cold models. Give each model its own measured recommendation for the selected hardware and workload.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Enumerate each configured engine/model pair once. Aliases do not repeat runs, and a model served by multiple engines retains every engine result.
- [ ] #2 Run candidates serially on shared GPUs. Save each result as it finishes, record individual failures and continue eligible work. Protect active serving requests.
- [ ] #3 After interruption, --resume skips completed matching candidates and reruns changed or incomplete ones. Match workload, hardware, model assets, runtime, effective configuration and limits.
- [ ] #4 Report the best verified configuration per model with its engine, runner-up, measured tradeoffs and unmet requirements. A single-model invocation under the same conditions produces the same score.
<!-- AC:END -->
