---
id: TASK-26
title: Close the gaps SPEC 5.3.1 still describes for container-native lifecycle
status: Waiting
assignee:
  - '@gpt-6.2-sol-release'
created_date: '2026-11-17T02:54:00Z'
updated_date: '2026-11-20T08:14:00Z'
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
Wake: independent review and hosted CI on [selective reset](https://github.com/ArborealML/Inference-Router/pull/48). Delivery owns qualification and integration. All 41 focused Release cases passed on 4c0681b with zero skips. Stop and removal recovery landed. Independent launch, post-publication mutation and durable host settlement remain open.
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
- [x] #10 A conflicting native stop response cannot authorize forced removal or replacement unless an independent observation proves the exact owned instance stopped. Pin the refusal and verified-state cases and reconcile the documented 409 behavior.
- [x] #11 After ambiguous stop/removal, retain exact operation and immutable ID. Block force-removal, replacement and retry until independently proven terminal.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Latch failure by exact engine, canonical model, effective profile and configuration. Keep probe publication fenced until the failed claim marker clears, and deliver each waiter its own recorded outcome. At operator reset, enforce explicit startup opt-in, credential and current process/generation ETag at the latch change. Verify a valid reload-only credential receives 404 before and after reload. Clear only the exact target; preserve uncertain-stop and host fences.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The [image change](https://github.com/ArborealML/Inference-Router/commit/c30ca3e), [resource fixtures](https://github.com/ArborealML/Inference-Router/commit/bc63d48) and [stop conflict fix](https://github.com/ArborealML/Inference-Router/pull/41) cover the earlier checked criteria. [Provider-lifetime stop and removal recovery](https://github.com/ArborealML/Inference-Router/pull/47) passed 171 focused Release cases with zero skips and independent review. The landed tree matched the approved composition at 3a47cb4; [merge CI](https://github.com/ArborealML/Inference-Router/actions/runs/44697942609) passed all three jobs. The shared switch-completion code was revalidated before handoff.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
Decision on transport: use the Unix socket. Implement and test against the fake daemon; TASK-33 gates live laptop demonstration.
<!-- COMMENTS:END -->
