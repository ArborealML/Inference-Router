---
id: TASK-26
title: Close the gaps SPEC 5.3.1 still describes for container-native lifecycle
status: Preparing
assignee:
  - '@gpt-6.2-sol-shutdown'
created_date: '2026-11-17T02:54:00Z'
updated_date: '2026-11-18T20:33:00Z'
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
Complete the container-native lifecycle requirements in SPEC section 5.3.1. Verify image identity before mutation, supply launch resources, observe daemon state, and quarantine failed targets. TASK-110 owns exclusive host reconciliation.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 An image is verified by identity (Id, RepoDigests, platform), not by the tag resolving at all, and a mismatch refuses the switch with one actionable message
- [ ] #2 GPU device requests, mounts and resource limits are expressible in config and reach the create request
- [ ] #3 Exec frame demux and log reading exist behind the provider seam with a fake, and a non-zero exec exit is a failure
- [ ] #4 A failed switch is latched: a dispatcher wake-up cannot retry it, and only an explicit target reset or a relevant configuration change can clear that target’s latch
- [ ] #5 Daemon event subscription (or a documented polling substitute) replaces the assumption that nothing else changed the engine
- [ ] #6 The Windows named-pipe transport is implemented or explicitly declined in SPEC with the reason
- [ ] #7 After an uncertain start, reconcile the exact recorded operation and immutable container ID. Prove cleanup of the owned unit, or retain the host fence until reconciliation establishes the outcome.
- [ ] #8 A shipped native Docker example passes the config loader with every model selector removed from that engine. The shipped-sample load test passes and remaining delegated examples identify exec as legacy.
- [ ] #9 After A fails, repeated scheduler wakeups and cooldown expiry produce zero attempts for its engine/model/profile/config generation. Resetting A permits a fresh attempt without resetting unrelated targets (decision-62).
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Audit each criterion against the current Docker provider and fake-daemon tests. Separate proven behavior, a shipped loadable configuration, and the remaining target quarantine/recovery work. Identify a complete first change independent of host fencing, and submit the implementation plan for review.

Reconcile SPEC section 15 with decisions 1, 2 and 56. Document daemon permission refusal and the boundary between router-owned launch settings and image entrypoint flags. Keep outstanding GPU, mount, image and readiness requirements on their implementation rows.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Provider fixes landed in 18d7279. Use fake daemon responses and preserve the loader refusal for mixed ownership. The shipped native sample must remove the selectors from the same engine and pass sample validation. TASK-119 owns the SPEC walkthrough. Align README ownership versus image checks, integration terminology and the safe-smoke explanation with the shipped sample. The AGENTS.md smoke guidance landed in fef8789. TASK-33 records live-proof permissions.

External review dispatched for native image identity and create-by-immutable-ID. The worker audited all nine criteria at b65aab7. The current preflight discards the inspect result and creates by configured tag. Host fencing and proof of target authority remain separate work.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T07:54:00Z

---

Route question (TASK-20) is answered: unix socket, daemon reachable by this account, no TLS endpoint. All seven criteria here stay buildable against the fake daemon handler, so this task is not blocked by anything. What it cannot do is *show* a lifecycle transaction against a live daemon; that permission is now TASK-33 and it gates only the demonstration, not the implementation. Recorded so nobody re-derives the route question.

---
<!-- COMMENTS:END -->
