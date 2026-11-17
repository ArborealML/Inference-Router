---
id: TASK-1
title: >-
  Native engine-lifecycle provider: talk to Docker directly, replace the
  shell-out selector
status: In Progress
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-16T20:39:00Z'
updated_date: '2026-11-17T02:34:00Z'
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
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Docker access goes through a swappable seam with a test fake (no daemon required)
- [ ] #2 All admissibility checks run before any container is stopped or started
- [ ] #3 Containers without our launcher labels are never stopped
- [ ] #4 Readiness failure is a hard error with one actionable message; no silent fallback
- [ ] #5 NativeAOT publish stays trim-clean (no reflection, no runtime provider discovery)
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
<!-- SECTION:NOTES:END -->

## Dependencies still open (decisions, not work)

Owner must record: (a) the posture when the container socket is unreachable or permission is denied —
recommended: refuse and report as `lifecycle_privilege_denied`, never elevate; (b) where engine launch
tuning lives — recommended hybrid: the router owns identity, ports, mounts, GPU device requests and
readiness, the image entrypoint owns engine flags and surfaces them through the health probe. Both are
written as SPEC 15.1 items 4 and 5 so the answer lands in the repo.
