---
id: TASK-26
title: Close the gaps SPEC 5.3.1 still describes for container-native lifecycle
status: In Progress
assignee:
  - '@gpt-6.2-sol-lifecycle'
created_date: '2026-11-17T02:54:00Z'
updated_date: '2026-11-20T07:23:00Z'
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
Lifecycle is implementing offline recovery for an exact incumbent container stop or removal issued before runtime readiness is committed. An uncertain outcome keeps the fence in place. Independent create/start and authority after publication remain open. Selective native reset waits for this change to land and for both workers to recheck the shared completion code.
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
- [ ] #11 After ambiguous stop/removal, retain exact operation and immutable ID. Block force-removal, replacement and retry until independently proven terminal.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Use the validated full container ID consistently across issue, history, readback and the final readiness decision. Treat uppercase and lowercase spellings of the same hexadecimal ID as equal. Exercise a case-equivalent stop through the real provider path and contradictory inventory after an exact-ID lookup. Preserve ordinary restoration when no conflicting stop or removal occurred. Delivery qualifies and independently reviews the candidate. The amended plan passed independent review before implementation.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Native image, resource, sample and exact-ID conflict evidence landed in [the image change](https://github.com/ArborealML/Inference-Router/commit/c30ca3e), [the resource change](https://github.com/ArborealML/Inference-Router/commit/bc63d48) and [PR #41](https://github.com/ArborealML/Inference-Router/pull/41). The stop conflict slice passed 22 focused Release cases and merge CI. Next: review uncertain stop reentry and readiness publication against current main; in parallel, plan target-specific failure quarantine and reset for reload. A transport failure alone cannot prove absence or authorize retry.

Decision 77 permits exact operator reset with explicitly enabled credential reuse and a current process/generation precondition. The reset changes only the matching scheduler latch.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
Decision on transport: use the Unix socket. Implement and test against the fake daemon; TASK-33 gates live laptop demonstration.
<!-- COMMENTS:END -->
