---
id: TASK-26
title: Close the gaps SPEC 5.3.1 still describes for container-native lifecycle
status: Waiting
assignee:
  - '@gpt-6.2-sol-lifecycle'
created_date: '2026-11-17T02:54:00Z'
updated_date: '2026-11-20T00:41:00Z'
labels:
  - engine
  - lifecycle
  - security
milestone: m-4
dependencies: []
priority: medium
ordinal: 26000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Waiting for independent review of the exact-ID stop-conflict plan and addendum on 07e364c. Its verdict wakes Lifecycle. AC10 and related AC11 cover conflict, ambiguous stop, forced removal and reentry. Qualify with the fake daemon.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 An image is verified by identity (Id, RepoDigests, platform), not by the tag resolving at all, and a mismatch refuses the switch with one actionable message
- [x] #2 GPU device requests, mounts and resource limits are expressible in config and reach the create request
- [ ] #3 Exec frame demux and log reading exist behind the provider seam with a fake, and a non-zero exec exit is a failure
- [ ] #4 A failed switch is latched: a dispatcher wake-up cannot retry it, and only an explicit target reset or a relevant configuration change can clear that target’s latch
- [ ] #5 Daemon event subscription (or a documented polling substitute) replaces the assumption that nothing else changed the engine
- [ ] #6 The Windows named-pipe transport is implemented or explicitly declined in SPEC with the reason
- [ ] #7 After an uncertain start, reconcile the exact recorded operation and immutable container ID. Prove cleanup of the owned unit, or retain the host fence until reconciliation establishes the outcome.
- [x] #8 A shipped native Docker example passes the config loader with every model selector removed from that engine. The shipped-sample load test passes and remaining delegated examples identify exec as legacy.
- [ ] #9 After A fails, repeated scheduler wakeups and cooldown expiry produce zero attempts for its engine/model/profile/config generation. Resetting A permits a fresh attempt without resetting unrelated targets (decision-62).
- [ ] #10 A conflicting native stop response cannot authorize forced removal or replacement unless an independent observation proves the exact owned instance stopped. Pin the refusal and verified-state cases and reconcile the documented 409 behavior.
- [ ] #11 After ambiguous stop/removal, retain exact operation and immutable ID. Block force-removal, replacement and retry until independently proven terminal.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Implement the reviewed image-identity slice first. Validate complete configured pins in TOML and JSON, compare full image ID, named manifest digest and platform against daemon inspect, and create by immutable ID. Preserve config loading and resident forwarding. Unpinned managed launch fails before mutation. Fake mismatches must leave an owned incumbent untouched. Document the migration, with daemon permission refusal and the boundary between native settings and entrypoint flags as settled by decisions 1, 2 and 56.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The [image and selector-free sample](https://github.com/ArborealML/Inference-Router/commit/c30ca3e) passed 128 focused tests, 419 full Release tests and server Native AOT. Four deliberate regressions failed as expected. Coordinator preflight verifies manifest and platform; direct launch checks the configured ID alone. Production activation requires the coordinated path.

The [resource fixtures](https://github.com/ArborealML/Inference-Router/commit/bc63d48) passed 184 focused Release tests and server Native AOT. They verify Docker payloads for measured Qwen and script-derived Bonsai, native devices, NVIDIA requests, mounts, limits and network. Production activation awaits verified host authority, effective settings, serving-instance binding and uncertain-create recovery. External diff reviews dispatched. TASK-33 records live-proof permissions.

For both RemoveStopped settings, inject timeout, disconnect and ambiguous daemon reply after possible issue. Verify exact-ID stopped or absent evidence as appropriate to the operation. Running, wrong-ID or unavailable evidence keeps the fence. Count attempts across re-entry. Transport failure alone proves neither absence nor pre-issue refusal.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
Decision on transport: use the Unix socket. Implement and test against the fake daemon; TASK-33 gates live laptop demonstration.
<!-- COMMENTS:END -->
