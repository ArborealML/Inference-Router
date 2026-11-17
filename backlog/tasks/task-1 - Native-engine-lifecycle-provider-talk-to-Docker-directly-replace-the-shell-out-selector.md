---
id: TASK-1
title: >-
  Native engine-lifecycle provider: talk to Docker directly, replace the
  shell-out selector
status: Done
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-16T20:39:00Z'
updated_date: '2026-11-17T08:06:00Z'
labels: []
milestone: v0.9 - native engine lifecycle
dependencies:
  - TASK-7
  - TASK-8
ordinal: 1000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Replace the shell-out selector with a provider seam the router drives itself.

Approved shape (reconciled from two external reviews, digested in unpublished notes):
`IEngineLifecycleProvider` over an `EngineLifecycleCoordinator` that runs the transaction in the order
ownership-proof -> admission close -> cohort drain -> preflight revalidation -> stop owned peers ->
launch exact target -> readiness proof -> admission reopen, with campaign/selection locks,
single-flight per target, latched failures, an advisory journal, and no adoption of unowned resources.
First Docker provider: raw Docker Engine HTTP over the socket with **source-generated** JSON, pinned
API version, exec frame demux, image pin verification and **no pulls**; the CLI stays as an operator
fallback, and nothing in the router ever re-execs sudo. Serve `engines preflight` as a validate-only
verb before any mutation. Keep `select.exec` working as a delegated-proof provider, mutually exclusive
per model with native lifecycle.

**Dependencies still open (decisions, not work)**
Owner must record: (a) the posture when the container socket is unreachable or permission is denied —
recommended: refuse and report as `lifecycle_privilege_denied`, never elevate; (b) where engine launch
tuning lives — recommended hybrid: the router owns identity, ports, mounts, GPU device requests and
readiness, the image entrypoint owns engine flags and surfaces them through the health probe. Both are
written as SPEC 15.1 items 4 and 5 so the answer lands in the repo.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Docker access goes through a swappable seam with a test fake (no daemon required)
- [x] #2 All admissibility checks run before any container is stopped or started
- [x] #3 Containers without our launcher labels are never stopped
- [x] #4 Readiness failure is a hard error with one actionable message; no silent fallback
- [x] #5 NativeAOT publish stays trim-clean (no reflection, no runtime provider discovery)
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Seam first, Docker behind it:\n1. `IEngineLifecycleProvider`: PreflightAsync (validate only), ProbeAsync, StopOwnedAsync, StartAsync, WaitReadyAsync, plus ownership labels. Everything the coordinator does is expressed through the interface so a fake can drive it.\n2. `EngineLifecycleCoordinator`: the transaction order — ownership proof, admission close, cohort drain, preflight revalidation, stop owned peers, launch exact target, readiness proof, admission reopen. Single-flight per target, refuse rather than escalate, containers without our labels are never touched, an advisory journal line per step.\n3. `DockerLifecycleProvider`: raw Engine HTTP over the socket (unix socket or TCP+TLS), pinned API version, source-generated JSON, image-pin verification, no pulls ever, exec frame demux kept out of the hot path. Refusal when the socket is unreachable is one actionable message.\n4. `select.exec` stays working as a delegated provider, mutually exclusive per model with native lifecycle.\n5. Tests: fake provider drives the whole transaction (order, refusal on unlabelled containers, readiness failure = one message, no fallback); Docker provider tested against a fake HTTP handler with recorded requests, no daemon.\n6. SPEC 5.3.1 rewrite + revlog; `engines preflight` stays validate-only.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Blocked on two decisions (decision-1 authorization model, decision-2 launch-tuning boundary) and one external design review whose result is held outside this repository; the plan will be written into this task before implementation starts.

Blocked on TASK-20 — the next move is the lifecycle mechanism and authorisation decision, which belongs to a human. Work here resumes when that task is closed.

Unblocked by checking rather than by a new decision: the provider shape is settled (raw Engine HTTP over a swappable seam), and the two decisions that used to gate it — refuse rather than escalate on ownership, and launch tuning — are recorded. Every acceptance criterion on this task can be met against a test fake with no daemon present, so implementation does not wait on anything. Only end-to-end verification against a real Docker host needs the daemon-route question answered.

First slice in: the provider seam (ownership proof, preflight, stop-what-we-own, launch), the coordinator that owns that ordering, and the existing selector path wrapped as the delegated provider with its behaviour unchanged. Seven fake-backed tests pin the order, the refusal-before-mutation, the never-stop-what-we-do-not-own rule and single-flight per engine. Admission-close and cohort-drain are not in this slice and are a task of their own: draining to zero today would wait on the very request that asked for the switch.

Seam, coordinator, delegated-exec provider and the native Docker provider are in main (two commits). AC #1 tested against a fake daemon; #2 holds for the checks that exist today — reachability, pinned API version, image presence — before anything is stopped or started; #3 tested both directions (a listed-but-unlabelled container is never handed to stop, and a failed ownership query refuses the whole transaction); #4 tested end to end, including that the ticket fails rather than being served elsewhere; #5 verified by zero trim or warning lines on both AOT publishes. Still open in this task, and listed in SPEC 5.3.1 rather than hidden: image identity pinning, mounts and GPU requests, exec frame demux, logs and daemon events, the Windows pipe transport, latched failures with no automatic retry, and the admission-close/cohort-drain step which is TASK-25. An external review of these two commits is in flight; it targets container-ownership safety, injection in the label filter, and calls that could hang forever. Nothing from it is adopted until verified against this code.

Done with all five acceptance criteria verified against tests and both AOT publishes. What the reviewed design describes beyond this task lives in TASK-25 (reservation, admission close, cohort drain) and TASK-26 (image identity, GPU and mounts, exec/logs/events, latched failures, Windows transport). An external review of the two commits is still in flight: a confirmed finding becomes a task or a fix in the next commit, not a reopening of this one.

Proof, added on the board rule that closed work names its evidence: lifecycle mutations moved behind the provider seam in one fixed order (commit 1191525) and the router loading a model in Docker itself, with proof of what it owns, in commit c2e7e42 - linked: [1191525](https://github.com/ArborealML/Inference-Router/commit/1191525), [c2e7e42](https://github.com/ArborealML/Inference-Router/commit/c2e7e42). Design authority: [SPEC 5.3.1](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md#L482) and the [native-lifecycle disposition in SPEC 15](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md#L1353). Tests that stand in for a daemon: [DockerLifecycleProviderTests.cs](https://github.com/ArborealML/Inference-Router/blob/main/tests/Arboreal.InferenceRouter.Tests/DockerLifecycleProviderTests.cs), [LifecycleCoordinatorTests.cs](https://github.com/ArborealML/Inference-Router/blob/main/tests/Arboreal.InferenceRouter.Tests/LifecycleCoordinatorTests.cs). Live-daemon verification is explicitly not claimed here - that permission is TASK-33.
<!-- SECTION:NOTES:END -->
