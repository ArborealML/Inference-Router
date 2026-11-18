---
id: TASK-96
title: >-
  Name the observation that withdrew health when a probe answer cannot land
  during a claim
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T09:21:00Z'
updated_date: '2026-11-18T19:29:00Z'
labels:
  - concurrency
milestone: m-2
dependencies:
  - TASK-76
priority: medium
ordinal: 95000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The commit review of d6cfb38 pressed the provenance bit TASK-61 shipped against the sequence it decides, and this seat re-measured the hole by reading. A proves an engine and holds its proof; B claims another model, so the marker goes up and health goes unknown with the bit naming B as author. A probe begun before B is version-fenced; one begun during B finds health already false and publishes nothing — PublishProbeFailure early-returns on already-unknown health. So while a claim is in flight the engine dying cannot be recorded at all, and A resumes told Ready on an engine that can no longer answer: a request that dies upstream instead of a refusal. The pin that shipped covers only a negative committed before B claims. Telling who withdrew health needs an evidence epoch — which observation, at which switch phase — the same substrate the gauge row wants; this row states the requirement and that row decides the shape.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A pin reproduces the sequence — own proof, a later claim, the engine dying during it — and shows the waiting caller answered Ready on an engine that can no longer answer.
- [ ] #2 Either the probe negative lands attributed to the probe, without letting a stale poll withdraw a switch proof, or SPEC 5.5 names the interval and its cost in the same commit.
- [ ] #3 The chosen shape is recorded as a decision before code, and the gauge row either reuses it or says why it does not.
<!-- AC:END -->
