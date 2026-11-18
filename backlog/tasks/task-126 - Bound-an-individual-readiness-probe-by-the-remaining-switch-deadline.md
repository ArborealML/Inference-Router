---
id: TASK-126
title: Bound an individual readiness probe by the remaining switch deadline
status: In Progress
assignee:
  - '@gpt-6.2-sol-shutdown'
created_date: '2026-11-18T20:43:00Z'
updated_date: '2026-11-18T23:09:00Z'
labels: []
milestone: m-4
dependencies: []
priority: high
ordinal: 124000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The readiness loop checks its deadline before calling the state and model endpoints. A pending HTTP call can outlive the remaining budget. Preserve external cancellation and fail the attempt through its existing terminal policy.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Hold a state probe and then a models probe separately past the remaining switch budget. Each attempt settles within the named bound and produces one terminal result.
- [ ] #2 A delayed successful reply after timeout cannot publish readiness or revive a retired attempt. A healthy alternate can still make progress.
- [ ] #3 Return an enrolled refusal through RunSwitchAsync after expiry or shutdown has won. The caller retains the original terminal cause and the feed records one terminal event, with no second capability change, completion, or dispatch wake.
- [ ] #4 Exercise successful guarded publication with hooks that verify both short locks are released. A joined waiter woken by shutdown or expiry receives its committed result only after the outcome event is delivered.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Use one monotonic readiness budget across state headers and body, model discovery, and poll delay. Separate observation from publication so a late or cancellation-ignoring handler cannot publish gauges or readiness after terminal settlement. Preserve shutdown precedence and the last completed mismatch diagnosis. Use bounded held handlers and a controllable clock to exercise each wait, cancellation races and alternate recovery. Coordinate with TASK-68 on snapshot ownership.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The state reader must return observations without publishing. Check the monotonic deadline at the guarded commit. The same claim owns proof and the terminal result. Deliver hooks outside the admission and transition locks. A late reader may clean up its resources. The standalone ownership correction builds the runtime; the focused test assembly stopped at CA2016 in its cancellation-ignoring fixture. No tests ran on that revision. Enrolled named tiers require a sufficient measured model window and positive measured total slots. The legacy slot fallback is separate.
<!-- SECTION:NOTES:END -->
