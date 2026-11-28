---
id: TASK-190
title: >-
  Survive an engine transition: drain before teardown, and never splice a
  continuation
status: Preparing
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-25T18:19:00Z'
updated_date: '2026-11-28T11:08:00Z'
labels: []
milestone: m-18
dependencies:
  - TASK-206
priority: high
ordinal: 182000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
When a switch is cancelled or a drain cannot finish, the router today leaves the machine in a state nobody owns: an applied-but-uncommitted launch, a pending reservation that blocks unrelated later starts, and a bad instance that later tickets relaunch one by one. This task makes cancellation mean drain-before-teardown with a durable disposition: the logical cancel lands promptly, physical ownership is released only after reconciliation, and anything unreconciled stays fenced rather than reading as vacant.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 One lifecycle owner fences admission atomically with reservation, so no dispatch races a teardown and an older generation cannot affect a new one.
- [ ] #2 Drain expiry preserves running inference and keeps the affected generation fenced. Reopening requires fresh authorization against the exact incumbent and reconciled physical uses, including after controller restart.
- [ ] #3 A container replaced out of band between two readiness probes cannot have its narrower published capacity suppress an owed relaunch, or the unprotected interval is documented in SPEC.
- [ ] #4 A cancelled or expired switch leaves exactly one durable disposition naming the operation, its generation and the instance; an unreconciled instance stays fenced under recovery ownership and is never read as vacant, by a restart or a second cancel.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Cancellation wins logically and promptly: the ticket is cancelled, its queue member released, and the caller answered through the existing cancelled mapping — no new refusal token, because the switch reason reaches caller-visible bytes. Physical teardown is a separate owned act: drain before relinquishing physical ownership, and where a drain cannot finish, keep the instance under a durable recovery fence instead of settling it as vacant. Sequence: a cancellation that wins before issue may abort a Prepared operation only if issue was impossible; after issue, reconcile the same operation and never create a replacement; after an applied launch, keep the route closed and either retain ownership or schedule exact-instance disposal through its own disposition record, persisted before any I/O and linked to the launch operation. Never turn unknown into vacant on expiry or restart. Bound the drain with one total budget starting when cancellation wins, initially 300 seconds, documented in SPEC; at expiry stop awaiting but keep the task, resources and exclusion under recovery ownership. Persist the disposition as a separate record: no mandatory field on the reservation state, whose restore rejects unknown members, and no change to what the checkpoint plan digest hashes. Fix the real local path first: the provider keeps the pending launch only in memory, and local recovery never reads the reservation. Two commits: the durable disposition with recovery and migration tests; then the cancellation winner, drain budget, ownership transfer and handoff.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Logical cancellation can complete while the issued physical transaction drains. Drain failure cannot overwrite a published stopped or cancelled outcome. Preserve the generation fence across timeout, late mutation and the group-specific quarantine behavior covered by TASK-198.

[Stock serving recovery](https://github.com/ArborealML/Inference-Router/pull/103) qualified the serving broker. The separate successor-operation journal can retain an appended entry whose external head update was never confirmed. Exercise restart after both possible head outcomes. Keep the domain fenced until it can recover the disposition of that exact operation without issuing a second mutation.
<!-- SECTION:NOTES:END -->
