---
id: TASK-20
title: 'Decision: may the router reach the Docker daemon, and by what route'
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T02:18:00Z'
updated_date: '2026-11-17T05:55:00Z'
labels:
  - engine
  - lifecycle
dependencies: []
priority: high
ordinal: 20000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->

The mechanism is settled — raw Engine HTTP over the Docker socket, with the docker CLI kept as an operator fallback — and authorisation (refuse rather than escalate) and launch tuning are already decisions on this board. What is missing is a fact about the machine: whether the router is allowed to talk to the daemon on the inference host, and if so whether that is the unix socket or a TLS TCP endpoint. This gates real-engine verification of the lifecycle provider, not the provider itself, which is written against a test fake.

What each answer changes: **unix socket** or **tcp+tls** means the native provider is verified end to end against a real daemon and TASK-26 proceeds as written; **no daemon access** narrows the shipped route to the delegated (exec) provider, and the container-native work in TASK-26 stops being worth building out. What it unblocks: the end-to-end verification half of TASK-26 — every other criterion there is testable against a fake daemon handler, so no task is blocked outright, but none of that verification can be *shown* until this is answered.

**Links**
- What the provider does without a daemon: [SPEC §5.3.1](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md#L420).
- Read-only probe, safe config: `dotnet run --project src/Arboreal.InferenceRouter.Cli -- engines preflight -c samples/router.safe-smoke.toml` — [samples/router.safe-smoke.toml](https://github.com/ArborealML/Inference-Router/blob/main/samples/router.safe-smoke.toml). Never run the configs that carry lifecycle commands.
- Fake-daemon tests that stand in until the route is known: [DockerLifecycleProviderTests.cs](https://github.com/ArborealML/Inference-Router/blob/main/tests/Arboreal.InferenceRouter.Tests/DockerLifecycleProviderTests.cs).

**Do this (about 2 minutes)**
The mechanism is already decided — raw Engine HTTP over the Docker socket, with the CLI as an operator
fallback — and the authorisation and launch-tuning questions are answered in decisions 1 and 2. One thing
is still missing, and it is about the machine rather than the design:

- Is the router allowed to reach the Docker daemon on the inference host, and by what route: the unix
  socket path, or a TLS TCP endpoint?
- If it is not allowed at all, say so and the work narrows to the delegated (shell-out) provider.

Answer format: `TASK-20: unix socket`, `TASK-20: tcp+tls`, or `TASK-20: no daemon access`. Keep the real
path off the public board; a placeholder is enough here and the real value stays in the untracked
config.

<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The daemon-access answer is given in one line: unix socket, tcp+tls, or no daemon access
- [ ] #2 If the answer is no daemon access, the narrowing is recorded on the board: the native provider is not built out further and the delegated path stays the shipped route
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Acceptance criteria rewritten on the board audit. As written they read as a topic rather than the question this task asks: #1 (mechanism chosen, SPEC section agreed) and #2 (authorisation posture for a container the router did not start) were both already settled — the mechanism is in SPEC 5.3.1, the authorisation posture is decision-1 (refuse, never escalate) and is implemented in the Docker provider, which refuses and says so rather than re-execing under sudo. Neither criterion could ever be closed by answering this task. The criteria now name the one fact the task is actually waiting for.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T05:27:00Z

---

Board audit: title, steps, cost and answer format were already here and are unchanged. Only the acceptance criteria and the description's consequences/what-it-unblocks needed writing down.

---

created: 2026-11-17T05:55:00Z

---

Owner instruction: the @claire queue is being worked by agents. Reassigned here because the outstanding item is a **fact about the machine**, not a preference: whether the router user can reach the daemon, and over which transport, is establishable by a read-only probe (the preflight verb is validate-only and mutates nothing). If no route is reachable, the recorded answer is "no daemon access" and TASK-26 narrows to the delegated provider. What remains an operator right, not an agent judgement, is *permitting* a new socket route on a host the router does not own: if establishing the fact requires granting group membership or opening a TLS listener, that grant stops the task and comes back to @claire as a one-line approval.

---
<!-- COMMENTS:END -->
