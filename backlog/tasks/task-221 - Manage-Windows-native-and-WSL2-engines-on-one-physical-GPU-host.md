---
id: TASK-221
title: Manage Windows-native and WSL2 engines on one physical GPU host
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-28T11:58:00Z'
labels: []
milestone: m-18
dependencies:
  - TASK-77
priority: medium
ordinal: 220000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
A later Windows peer runs Strata natively and HyperQwen under WSL2. One host manager must switch between their configured profiles while both runtimes share physical hardware. The first two-host cluster qualification uses Linux. Record actual Windows-host fit and context limits when that peer becomes available.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The same host-manager contract identifies and controls both runtime instances and reports separately the catalog, the serving process, the observed context limit and shared GPU occupancy.
- [ ] #2 With native Strata serving, the original queued HyperQwen request drains it and streams on the verified WSL2 instance. Repeat in reverse and observe one physical owner throughout.
- [ ] #3 A request exceeding the WSL2 profile limit stays ineligible there. Unknown occupancy, host restart or ambiguous launch retains the domain fence and prevents duplicate execution.
- [ ] #4 Controlled provider tests exercise both process environments. Live qualification records the actual Windows and WSL2 versions, GPU residency, context, cancellation and successful original-ticket delivery.
<!-- AC:END -->
