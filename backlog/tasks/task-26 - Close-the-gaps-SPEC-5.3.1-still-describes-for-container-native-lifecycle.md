---
id: TASK-26
title: Close the gaps SPEC 5.3.1 still describes for container-native lifecycle
status: Waiting
assignee:
  - '@gpt-6.2-sol-release'
created_date: '2026-11-17T02:54:00Z'
updated_date: '2026-11-20T18:45:00Z'
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
Wake: independent reviews and integrated qualification for Docker exec and log handling on landed SPEC .109. Candidate 238682c passed 51 focused Release tests with zero skips, including stream disposal checks. Delivery owns integration. Lifecycle is released to prepare the separate daemon change-detection criterion against existing evidence. [Selective reset](https://github.com/ArborealML/Inference-Router/pull/48) remains landed.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Verify image Id, RepoDigests and platform independently of tag resolution. A mismatch refuses the switch with one actionable message.
- [x] #2 GPU device requests, mounts and resource limits are expressible in config and reach the create request
- [ ] #3 Exec frame demux and log reading exist behind the provider seam with a fake, and a non-zero exec exit is a failure
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
Request stdout and stderr explicitly at exec creation and pin the start request. Verify immutable ownership and demultiplex binary frames with finite byte ceilings for output, metadata and errors. Require exact terminal inspect status and exit code. Start one deadline before acquiring the provider gate. Preserve unknown interrupted-exec handling, pending stop/removal fences and borrowed handler ownership. Use DockerNativeDiagnosticsTests to distinguish status, size, timeout, sends and disposal on the fake handler.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The [image change](https://github.com/ArborealML/Inference-Router/commit/c30ca3e), [resource fixtures](https://github.com/ArborealML/Inference-Router/commit/bc63d48) and [stop conflict fix](https://github.com/ArborealML/Inference-Router/pull/41) cover the earlier checked criteria. [Provider-lifetime stop and removal recovery](https://github.com/ArborealML/Inference-Router/pull/47) passed 171 focused Release cases with zero skips and independent review. The landed tree matched the approved composition at 3a47cb4; [merge CI](https://github.com/ArborealML/Inference-Router/actions/runs/44697942609) passed all three jobs. The shared switch-completion code was revalidated before handoff.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
Decision on transport: use the Unix socket. Implement and test against the fake daemon; TASK-33 gates live laptop demonstration.
<!-- COMMENTS:END -->
