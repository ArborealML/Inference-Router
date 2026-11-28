---
id: TASK-122
title: Admit requests from either ingress into one fleet queue
status: To Do
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-18T20:32:00Z'
labels: []
milestone: m-6
dependencies:
  - TASK-77
  - TASK-203
  - TASK-204
priority: high
ordinal: 120000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
One router writer owns the fleet queue and assignment. Additional ingress forwards to the same authority; no second pending queue or distributed consensus. Use fixed configured host/model placement first.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Send concurrent requests through two ingress routers. Each accepted request has one queue entry, at most one active delivery claim and one terminal result.
- [ ] #2 Exercise disconnection during claim handoff. After uncertain upstream acceptance, a successor records one indeterminate terminal result and sends zero additional inference requests.
- [ ] #3 Test fleet capacity, cancellation and the no-option deadline through both ingress endpoints, including an unreachable host.
- [ ] #4 Both ingress paths preserve policy, ticket, sequence and limits; the forwarder never owns independent pending work.
- [ ] #5 A slow/refused or unreachable peer cannot block another admissible provider; exact one-time reservation and unknown-acceptance fixtures prove isolation.
- [ ] #6 Forwarder authenticates to the writer and preserves verified caller scope, ticket cancellation and deadlines; no caller-supplied header grants owner or manager authority.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Fixture fixed placement can use explicit finite request deadlines; TASK-51's default deadline is a live cluster release gate, not a prerequisite to building forwarding. No new identity service.
<!-- SECTION:NOTES:END -->
