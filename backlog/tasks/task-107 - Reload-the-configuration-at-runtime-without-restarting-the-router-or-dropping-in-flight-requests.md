---
id: TASK-107
title: >-
  Reload the configuration at runtime without restarting the router or dropping
  in-flight requests
status: Waiting
assignee:
  - '@gpt-6.2-sol-stream-delivery'
created_date: '2026-11-18T17:42:00Z'
updated_date: '2026-11-20T03:37:00Z'
labels:
  - config
  - lifecycle
  - serve
milestone: m-8
dependencies: []
priority: high
ordinal: 105000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wake: independent code and fixture reviews of the qualified reload correction at 95dbdd6. Delivery owns disposition and integration. Selective native quarantine reset remains open.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 One reload trigger is named and works — signal, admin route, or watcher — and a pin drives it end to end
- [ ] #2 SPEC states what reload may change and what still needs a restart, as a table a reader can check
- [ ] #3 A reload landing mid-switch takes effect at a named boundary; no lifecycle transaction sees two configs, pinned
- [ ] #4 A reload that removes an engine with in-flight tickets follows a named rule (drain or refuse), pinned
- [ ] #5 A stream running across a reload completes on the snapshot it started with, asserted on bytes as in TASK-82
- [ ] #6 A reload that fails validation leaves the running config untouched and answers in the single lowercase error shape
- [ ] #7 decision-50 is restated in the same push, its read-only-after-boot premise bounded and the revalidation rule attached
- [ ] #8 A reload unrelated to a quarantined target leaves its latch intact. Resetting or changing one target permits its next attempt while other quarantined targets remain latched.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Require the expected process identity and configuration generation on apply. Validate before publication. Serialize generation publication with enqueue, exact take and closing. Publish enqueue events before dispatch or terminal events. Fence retired engines against new uses and retain physical inference accounting through detached cleanup and final publication. A reported-only default requires a retained, healthy external engine advertising it through commit. Managed defaults require immutable model declarations. Exercise the authenticated HTTP entry, invalid and stale apply, active stream bytes and switch identity, busy refusal, shutdown and event ordering. Verify source-generated serialization under Native AOT.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The original [PR #43](https://github.com/ArborealML/Inference-Router/pull/43) head e5d4576 passed 347 focused Release tests and [hosted CI](https://github.com/ArborealML/Inference-Router/actions/runs/44666394509). Review accepted the SPEC reconciliation and requested corrections for pending queue entries blocking eligible peers, concurrent disposal waiting for the same cleanup, and routing and event ordering under races. The correction at 95dbdd6 passed 210 focused Release tests with a warning-free build and zero server Native AOT trim warnings. Hosted results remain attributed to the original head.
<!-- SECTION:NOTES:END -->
