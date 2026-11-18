---
id: TASK-26
title: Close the gaps SPEC 5.3.1 still describes for container-native lifecycle
status: In Progress
assignee:
  - '@gpt-6.2-sol-shutdown'
created_date: '2026-11-17T02:54:00Z'
updated_date: '2026-11-18T21:10:00Z'
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
- [x] #1 An image is verified by identity (Id, RepoDigests, platform), not by the tag resolving at all, and a mismatch refuses the switch with one actionable message
- [ ] #2 GPU device requests, mounts and resource limits are expressible in config and reach the create request
- [ ] #3 Exec frame demux and log reading exist behind the provider seam with a fake, and a non-zero exec exit is a failure
- [ ] #4 A failed switch is latched: a dispatcher wake-up cannot retry it, and only an explicit target reset or a relevant configuration change can clear that target’s latch
- [ ] #5 Daemon event subscription (or a documented polling substitute) replaces the assumption that nothing else changed the engine
- [ ] #6 The Windows named-pipe transport is implemented or explicitly declined in SPEC with the reason
- [ ] #7 After an uncertain start, reconcile the exact recorded operation and immutable container ID. Prove cleanup of the owned unit, or retain the host fence until reconciliation establishes the outcome.
- [x] #8 A shipped native Docker example passes the config loader with every model selector removed from that engine. The shipped-sample load test passes and remaining delegated examples identify exec as legacy.
- [ ] #9 After A fails, repeated scheduler wakeups and cooldown expiry produce zero attempts for its engine/model/profile/config generation. Resetting A permits a fresh attempt without resetting unrelated targets (decision-62).
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Implement the reviewed image-identity slice first. Validate complete configured pins in TOML and JSON, compare full image ID, named manifest digest and platform against daemon inspect, and create by immutable ID. Preserve config loading and resident forwarding. Unpinned managed launch fails before mutation. Fake mismatches must leave an owned incumbent untouched. Document the migration, with daemon permission refusal and the boundary between native settings and entrypoint flags as settled by decisions 1, 2 and 56.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Provider fixes landed in 18d7279. Use fake daemon responses and preserve the loader refusal for mixed ownership. The shipped native sample must remove the selectors from the same engine and pass sample validation. TASK-119 owns the SPEC walkthrough. Align README ownership versus image checks, integration terminology and the safe-smoke explanation with the shipped sample. The AGENTS.md smoke guidance landed in fef8789. TASK-33 records live-proof permissions.

[Native image verification](https://github.com/ArborealML/Inference-Router/commit/c30ca3e) checks the full local image ID, named repository digest and platform before mutation. Create names the immutable image ID. The shipped selector-free native example passed CLI validation. The coordinator ran 128 focused and 419 full Release tests, then published the server with Native AOT. Four isolated regressions weakened ID, repository, platform variant and create-reference checks. Each failed its intended fixture; restoring source passed all 25 targeted cases. Production and fixture reviews are in flight. Device requests, mounts and resource limits are the next implementation slice.

Resource configuration plan sent for review against the archived Qwen payload and Bonsai script. It covers Linux device mappings, read-only binds, CPU, memory, swap and core limits. Qualification must distinguish an observed container from a proposed launch.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
Decision on transport: use the Unix socket. Implement and test against the fake daemon; TASK-33 gates live laptop demonstration.
<!-- COMMENTS:END -->
