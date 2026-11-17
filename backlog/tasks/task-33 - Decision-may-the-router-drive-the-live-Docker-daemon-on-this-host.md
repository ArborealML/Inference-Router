---
id: TASK-33
title: 'Decision: may the router drive the live Docker daemon on this host'
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T07:53:00Z'
updated_date: '2026-11-17T11:04:00Z'
labels:
  - engine
  - lifecycle
  - security
dependencies: []
priority: medium
ordinal: 32000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Who acts next: you, with one line.** Everything factual is already settled (TASK-20): the daemon is reachable
from this account over the standard unix socket, and the router's native provider talks to it without shelling out.
What is left is a permission, not a fact, and it is yours because it changes shared infrastructure: whether the
router is allowed to start, stop and remove containers on the inference host as part of a live end-to-end test.

**Why it is on the board at all.** Every acceptance criterion in TASK-26 can be built and tested against a fake
daemon handler, so no code work waits on this. What waits on it is the ability to *show* the lifecycle provider
working against a real daemon — a model switch that actually drains one engine and starts another. Per the working
contract, a lifecycle run on this machine is a mutation of shared infrastructure, so it is not something an agent
does on its own initiative.

**What changes under each answer.** Allow -> the main session runs one lifecycle transaction against the live
daemon with a config that names real engines, reports what it started and stopped, and TASK-26 and TASK-1
verification stops resting on test fakes. Decline -> the native provider stays verified against fakes only, the
delegated (exec) provider stays the route that is actually exercised here, and the container-native work in
TASK-26 keeps being built against a fake forever, which is a posture worth stating in SPEC rather than leaving
implicit.

**Do this (about 1 minute).** Answer one line on this task: `allow one lifecycle transaction on this host`, or
`no live daemon runs: keep it on fakes`. Nothing else is needed to unblock the verification work.

**Links**
- The route, already established: [TASK-20] (https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-20%20-%20Decision-how-the-router-starts-and-stops-engines.md).
- Provider behaviour and its fakes: [SPEC §5.3.1](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md#L420), [DockerLifecycleProviderTests.cs](https://github.com/ArborealML/Inference-Router/blob/main/tests/Arboreal.InferenceRouter.Tests/DockerLifecycleProviderTests.cs).
- Read-only check an agent can run any time, no mutation: `dotnet run --project src/Arboreal.InferenceRouter.Cli -- engines preflight -c samples/router.safe-smoke.toml` with [samples/router.safe-smoke.toml](https://github.com/ArborealML/Inference-Router/blob/main/samples/router.safe-smoke.toml). Never smoke-test a config that carries lifecycle commands.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Answer in one line: allow one lifecycle transaction on this host, or no live daemon runs and verification stays on fakes
<!-- AC:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T10:58:00Z

---

Reassigned under decision-16: the permission is no longer the owner's to type, so the task becomes a scoped act with a report. Answer recorded: **allow one lifecycle transaction on this host**, bounded to a named disposable engine that no other session is using, with the config committed as a sample-like file under `.local/`, and a report naming what was started, stopped and removed and how long each step took. That is a mutation of shared infrastructure on the owner's machine, so it stays reportable in the commit and in SPEC's verification wording, and it is reversible by `docker` inspection. If the run cannot be made bounded — no idle engine, or an unknown engine holding a slot — stop and put it back on the board rather than choosing which engine to disturb.

---

created: 2026-11-17T11:04:00Z

---

Wording fix on the previous comment: the config for that run is a working file kept **out of the repository** (a committed sample would print engine endpoints and lifecycle argv, which the hygiene rule in AGENTS.md forbids). The record is the report — what was started, stopped and removed, and how long each phase took — plus a SPEC sentence naming what is now verified against a live daemon rather than a fake.

---
<!-- COMMENTS:END -->
