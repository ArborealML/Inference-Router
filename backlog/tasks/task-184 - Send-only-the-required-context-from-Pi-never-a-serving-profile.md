---
id: TASK-184
title: Send only the required context from Pi; never a serving profile
status: Waiting
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-25T07:52:00Z'
updated_date: '2026-11-27'
labels: []
milestone: m-16
dependencies:
  - TASK-183
priority: high
ordinal: 176000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wake when TASK-183 lands additive numeric ingress. Pi separates conversation/compaction ceiling, intentional hard floor and actual per-request fit. Remove Pi's bundled physical-profile selection without changing old third-party wire semantics; policy-aware catalog/compaction belongs to TASK-209.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Both Pi outbound paths omit named/preferred profile in numeric mode; intentional numeric floor survives.
- [ ] #2 A 1M conversation ceiling does not force a 1M floor on a small automatic request; AIR computes/validates actual demand.
- [ ] #3 Picker shows numeric context and potential capability from existing or private-fixture catalog; resident allocation is separate.
- [ ] #4 Impossible explicit floor gets router refusal; preserve captured turns and existing compaction/warnings. Per-agent isolation is a separate release gate.
- [ ] #5 Restore, auxiliary calls, turn capture and priority survive; pinned Pi API and final outbound bytes are tested.
- [ ] #6 SPEC §§6 and 13 document additive migration; supported old clients still work. No server-side header retirement prerequisite.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Reuse catalog/session controls and existing tracked extension; use current/private-fixture capability catalog. TASK-209 owns policy-filtered catalog and private compaction; TASK-186/188 gate actual installed release and isolation. Preserve explicit third-party minima and old named semantics; retire only by explicit later migration decision.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Old plan sent the selected window on every call, defeating dynamic allocation. Installed migration remains unqualified.
<!-- SECTION:NOTES:END -->
