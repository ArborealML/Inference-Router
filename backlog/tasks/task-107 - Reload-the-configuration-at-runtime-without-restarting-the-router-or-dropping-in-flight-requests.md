---
id: TASK-107
title: >-
  Reload the configuration at runtime without restarting the router or dropping
  in-flight requests
status: Waiting
assignee:
  - '@gpt-6.2-sol-stream-delivery'
created_date: '2026-11-18T17:42:00Z'
updated_date: '2026-11-20T04:08:00Z'
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
Wake: TASK-26 supplies selective native failure quarantine and target reset. Runtime configuration reload landed in v0.12.102. The remaining criterion requires a reset or change to one native target to permit its next attempt while unrelated quarantines stay latched.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 One reload trigger is named and works — signal, admin route, or watcher — and a pin drives it end to end
- [x] #2 SPEC states what reload may change and what still needs a restart, as a table a reader can check
- [x] #3 A reload landing mid-switch takes effect at a named boundary; no lifecycle transaction sees two configs, pinned
- [x] #4 A reload that removes an engine with in-flight tickets follows a named rule (drain or refuse), pinned
- [x] #5 A stream running across a reload completes on the snapshot it started with, asserted on bytes as in TASK-82
- [x] #6 A reload that fails validation leaves the running config untouched and answers in the single lowercase error shape
- [x] #7 decision-50 is restated in the same push, its read-only-after-boot premise bounded and the revalidation rule attached
- [ ] #8 A reload unrelated to a quarantined target leaves its latch intact. Resetting or changing one target permits its next attempt while other quarantined targets remain latched.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Require the expected process identity and configuration generation on apply. Validate before publication. Serialize generation publication with enqueue, exact take and closing. Publish enqueue events before dispatch or terminal events. Fence retired engines against new uses and retain physical inference accounting through detached cleanup and final publication. A reported-only default requires a retained, healthy external engine advertising it through commit. Managed defaults require immutable model declarations. Exercise the authenticated HTTP entry, invalid and stale apply, active stream bytes and switch identity, busy refusal, shutdown and event ordering. Verify source-generated serialization under Native AOT.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The authenticated reload, generation and stream checks landed in [PR #43](https://github.com/ArborealML/Inference-Router/pull/43). SPEC §7 documents live and restart-only settings, retained definitions and prospective dispatch revalidation. Focused Release results were 347 on e5d4576, 210 on 95dbdd6 and 5 on de4f3da. Server Native AOT had zero trim warnings. Review corrections covered queue eligibility, shared cleanup, notification order and exact UTF-8 bytes. The sorted queue refuted the separate ordering finding. [CI on the merge](https://github.com/ArborealML/Inference-Router/actions/runs/44671719107) passed on 13fdd43, whose non-board tree matches the approved head.
<!-- SECTION:NOTES:END -->
