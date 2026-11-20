---
id: TASK-107
title: >-
  Reload the configuration at runtime without restarting the router or dropping
  in-flight requests
status: In Progress
assignee:
  - '@gpt-6.2-sol-stream-reload'
created_date: '2026-11-18T17:42:00Z'
updated_date: '2026-11-20T02:46:00Z'
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
Implementing authenticated reload of the startup configuration file. Decision 75 allows new defaults during active work and external topology changes only at router-wide idle. Existing streams and switches retain their starting configuration. Selective native quarantine reset remains open.
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
TASK-83 covers accepted config keys and runtime handlers. Decision-56 requires native engine selection. Review a portable admin preview/apply with expected generation; classify live, drain and restart-only settings. Keep streams and switches on their starting generation, reject stranded queues, and verify redaction and access. Consider 409 for stale generation and 422 for invalid changes, preserving the old config. Decide the wire contract in review before implementation. A delegated implementer delivers the diff and raw checks; the coordinator integrates. Socket-based proofs require a runner that can bind loopback.
<!-- SECTION:NOTES:END -->
