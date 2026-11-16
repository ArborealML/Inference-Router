---
id: TASK-1
title: >-
  Native engine-lifecycle provider: talk to Docker directly, replace the
  shell-out selector
status: To Do
assignee: []
created_date: '2026-11-16T20:39:00Z'
updated_date: '2026-11-16T20:41:00Z'
labels: []
milestone: v0.9 - native engine lifecycle
dependencies: []
ordinal: 1000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Implements the select-model logic inside AIR as a configuration-driven engine provisioning subsystem: container identity/ownership labels, preflight with no side effects, stop-other-backends policy, proven readiness (loaded model, exact context window, exact slot count, modality, in-container verification), staged-asset receipt checks, qualification manifests for experimental serving features, cross-process locks, and explicit privilege policy. The router calls the Docker API itself rather than shelling out to the docker CLI; the existing select.exec path remains as an escape hatch.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Docker access goes through a swappable seam with a test fake (no daemon required)
- [ ] #2 All admissibility checks run before any container is stopped or started
- [ ] #3 Containers without our launcher labels are never stopped
- [ ] #4 Readiness failure is a hard error with one actionable message; no silent fallback
- [ ] #5 NativeAOT publish stays trim-clean (no reflection, no runtime provider discovery)
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Blocked on two decisions (decision-1 authorization model, decision-2 launch-tuning boundary) and one external design review whose result is held outside this repository; the plan will be written into this task before implementation starts.
<!-- SECTION:NOTES:END -->
