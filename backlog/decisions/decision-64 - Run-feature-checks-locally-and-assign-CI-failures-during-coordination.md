---
id: decision-64
title: Run feature checks locally and assign CI failures during coordination
date: '2026-11-18T19:49:00Z'
status: accepted
---
## Context

CI builds Release and runs the full suite on main and pull requests. Local iteration needs fast feedback on the changed behavior. A subsequent green run can mask an intermittent failure.

## Decision

Implementers run focused local tests. The coordinator checks CI regularly, assigns each distinct unresolved failure to a named implementer at high priority, and keeps the failing run and verification condition on the task. A local full suite needs a stated measurement or a failure specific to the whole suite.

## Consequences

Repeated occurrences update one task. The coordinator verifies the fix on CI and schedules routine board updates together to reduce superseded runs. Explicit host-conditioned acceptance measurements retain their recorded controls.
