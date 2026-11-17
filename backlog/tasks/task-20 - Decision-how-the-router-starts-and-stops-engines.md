---
id: TASK-20
title: 'Decision: may the router reach the Docker daemon, and by what route'
status: To Do
assignee:
  - '@claire'
created_date: '2026-11-17T02:18:00Z'
updated_date: '2026-11-17T02:30:00Z'
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
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The mechanism is chosen and the SPEC section that describes it is agreed
- [ ] #2 The authorisation posture for a container the router did not start is stated (refuse, adopt, or refuse-and-report)
<!-- AC:END -->

## Do this (about 2 minutes)

The mechanism is already decided — raw Engine HTTP over the Docker socket, with the CLI as an operator
fallback — and the authorisation and launch-tuning questions are answered in decisions 1 and 2. One thing
is still missing, and it is about the machine rather than the design:

- Is the router allowed to reach the Docker daemon on the inference host, and by what route: the unix
  socket path, or a TLS TCP endpoint?
- If it is not allowed at all, say so and the work narrows to the delegated (shell-out) provider.

Answer format: `TASK-20: unix socket`, `TASK-20: tcp+tls`, or `TASK-20: no daemon access`. Keep the real
path off the public board; a placeholder is enough here and the real value stays in the untracked
config.
