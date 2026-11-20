---
id: TASK-26
title: Close the gaps SPEC 5.3.1 still describes for container-native lifecycle
status: In Progress
assignee:
  - '@gpt-6.2-sol-shutdown'
created_date: '2026-11-17T02:54:00Z'
updated_date: '2026-11-20T19:42:00Z'
labels:
  - engine
  - lifecycle
  - security
milestone: m-4
dependencies: []
priority: high
ordinal: 26000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Lifecycle handed off e875375 for the coordinated Docker 204 daemon-observation increment. Shutdown is implementing the separate supervised lost-start increment: preserve the exact container ID in an anchored record before Docker Start and keep the launch fenced on an unknown reply after reopen. [Docker diagnostics](https://github.com/ArborealML/Inference-Router/pull/53) landed at b0f8255. Full change detection and uncertain-start reconciliation remain open.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Verify image Id, RepoDigests and platform independently of tag resolution. A mismatch refuses the switch with one actionable message.
- [x] #2 GPU device requests, mounts and resource limits are expressible in config and reach the create request
- [x] #3 Exec frame demux and log reading exist behind the provider seam with a fake, and a non-zero exec exit is a failure
- [x] #4 A failed activation after native lifecycle transaction entry latches the target through dispatcher wakes until explicit target reset or relevant configuration change. Earlier claim-hook or scheduling rollback permits retry without reset.
- [ ] #5 Daemon event subscription (or a documented polling substitute) replaces the assumption that nothing else changed the engine
- [ ] #6 The Windows named-pipe transport is implemented or explicitly declined in SPEC with the reason
- [ ] #7 After an uncertain start, reconcile the exact recorded operation and immutable container ID. Prove cleanup of the owned unit, or retain the host fence until reconciliation establishes the outcome.
- [x] #8 A shipped native Docker example passes the config loader with every model selector removed from that engine. The shipped-sample load test passes and remaining delegated examples identify exec as legacy.
- [x] #9 After A fails, repeated scheduler wakeups and cooldown expiry produce zero attempts for its engine/model/profile/config generation. Resetting A permits a fresh attempt without resetting unrelated targets (decision-62).
- [x] #10 A conflicting native stop response cannot authorize forced removal or replacement unless an independent observation proves the exact owned instance stopped. Pin the refusal and verified-state cases and reconcile the documented 409 behavior.
- [x] #11 After ambiguous stop/removal, retain exact operation and immutable ID. Block force-removal, replacement and retry until independently proven terminal.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Sample the same immutable instance after the endpoint reads and before publishing readiness. Order the final check with issued stop/removal history. Distinguish retryable unknown observations from definite invalidation, including coherent created and removing states. For baseline and final inspections, prove timeout releases the provider gate and delivers one failure while the cancelled GET is still held. Exercise idle observation with a controlled timer and qualify NativeDaemonObservationTests through the fake provider.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The [image change](https://github.com/ArborealML/Inference-Router/commit/c30ca3e), [resource fixtures](https://github.com/ArborealML/Inference-Router/commit/bc63d48) and [stop conflict fix](https://github.com/ArborealML/Inference-Router/pull/41) cover the earlier checked criteria. [Provider-lifetime stop and removal recovery](https://github.com/ArborealML/Inference-Router/pull/47) passed 171 focused Release cases with zero skips and independent review. The landed tree matched the approved composition at 3a47cb4; [merge CI](https://github.com/ArborealML/Inference-Router/actions/runs/44697942609) passed all three jobs. The shared switch-completion code was revalidated before handoff.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
Decision on transport: use the Unix socket. Implement and test against the fake daemon; TASK-33 gates live laptop demonstration.
<!-- COMMENTS:END -->
