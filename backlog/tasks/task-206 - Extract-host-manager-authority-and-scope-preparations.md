---
id: TASK-206
title: "Extract host manager authority and scope preparations"
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-27'
labels: []
milestone: m-6
dependencies:
  - TASK-203
  - TASK-110
  - TASK-118
  - TASK-190
  - TASK-197
  - TASK-198
priority: high
ordinal: 206000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Extract the smallest physical-manager seam around EngineRuntime, LocalLifecycleGroup, capability publication and existing admission/cleanup. Ordinary construction has no enrollment; injected ExactTicketAdmission and lifecycle groups currently cannot compose. Do not introduce a second scheduler.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Combined, router-only and manager-only roles use one Native AOT build and equivalent local/remote authority semantics.
- [ ] #2 One host or fenced resource domain owns all physical mutation, actual capacity and cleanup; exact admission and lifecycle groups compose on the ordinary production path.
- [ ] #3 Preparation ownership and capacity are domain-scoped; hold one preparation and prove another domain admits and settles independently.
- [ ] #4 A snapshot is advisory; accept binds immutable operation/body, instance/recipe, generation and domain. Changed state returns a refusal, not silent reassignment.
- [ ] #5 Same-ID different content is rejected; lost acceptance, cancellation, restart and partition retain unresolved physical ownership. Stale messages cannot create fresh work after receipt retirement.
- [ ] #6 No remote container commands, recursive routing, competing pending queue, or speculative duplicate demand. Standalone and legacy forwarding remain qualified.
- [ ] #7 SPEC §§4, 5, 7, 12, 14 define role composition, admission and recovery; test injected and ordinary entry points.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Extract the existing in-process seam first. Bind exact invocation and prepare IDs to real host fencing, not inferred readiness. Use a bounded snapshot/accept-execute/cancel/status contract for managed peers, with durable acceptance only for unresolved host operations. Fix per-domain preparation scope; reuse settled physical cleanup. Verify manager separately before remote transport in TASK-77.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Existing TASK-64/142 offline evidence and same-host switch results are not enrolled production qualification.
<!-- SECTION:NOTES:END -->
