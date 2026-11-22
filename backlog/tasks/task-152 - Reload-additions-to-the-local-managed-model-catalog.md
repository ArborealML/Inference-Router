---
id: TASK-152
title: Reload additions to the local managed model catalog
status: Done
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-21'
updated_date: '2026-11-22T01:15:00Z'
labels: []
milestone: m-8
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Let an operator add managed engines to an existing local GPU group and add aliases to existing models through authenticated file reload. Preserve the serving process, resident and existing runtime state. Use the existing idle-only topology reload contract.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Startup accepts ordinary local groups with authenticated file reload enabled.
- [x] #2 An idle apply adds multiple managed engines and aliases in one generation, preserving the incumbent, existing runtime identities and quarantine state, with zero Docker mutations.
- [x] #3 Active, queued, switching or detached requests yield Busy. Invalid topology, alias conflicts, stale generation and cancellation before commit leave all live routes and group membership intact.
- [x] #4 After apply, an ordinary request selects a newly added model and streams through its native lifecycle group. Existing canonical names and aliases still route correctly.
- [x] #5 Integrate the reviewed candidate and link its hosted qualification.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Permit the existing file reload endpoint at ordinary local-group startup. Keep authenticated apply, expected instance/generation and commissioned-mode restrictions.
2. Extend candidate comparison only for fresh engine names joining existing local groups and additive aliases. Validate the full candidate topology and reject ambiguous aliases or changes to existing targets. Resolve added aliases through the published generation, including model discovery where aliases are exposed.
3. Share startup's native runtime construction. Stage additions without attaching them to a live group or starting probes. Under the existing router gate recheck idle, generation, cancellation and closing. Publish prepared membership, routes and aliases together without replacing the group's residency, drain or failure state. Activate observations after commit. Dispose unpublished runtimes on refusal.
4. Exercise the authenticated endpoint with a fake Docker daemon, including multiple additions to a populated group, unchanged resident and original-ticket cold dispatch. Run the focused reload and local lifecycle tests and Native AOT publish. Document additive idle reload in SPEC §7.7.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The authenticated idle apply accepted catalog additions while preserving the resident and existing routes. Focused reload and group tests passed. An isolated process applied the ten-model configuration and a repeated apply preserved the generation. The [merged Pi integration](https://github.com/ArborealML/Inference-Router/pull/75) includes the implementation and the [main CI](https://github.com/ArborealML/Inference-Router/actions/runs/44967781268) passed.
<!-- SECTION:NOTES:END -->
