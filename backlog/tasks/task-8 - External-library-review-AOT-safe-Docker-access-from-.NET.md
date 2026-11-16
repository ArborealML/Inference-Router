---
id: TASK-8
title: 'External library review: AOT-safe Docker access from .NET'
status: In Progress
assignee:
  - '@deep'
created_date: '2026-11-16T20:46:00Z'
labels:
  - external
dependencies: []
ordinal: 8000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
An external deep-review pass has been dispatched to settle which way the router should talk to Docker: the long-standing community client, the actively maintained fork used by cloud tooling, or hand-rolled Engine HTTP over the unix socket with source-generated JSON. The crux is whether each survives native AOT without reflection, plus unix-socket support, exec and events coverage, error fidelity between 'absent' and 'unreachable', and Engine API version pinning.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A recommendation with package names, versions and an AOT verdict
- [ ] #2 The chosen seam is faked in tests without a live daemon
<!-- AC:END -->
