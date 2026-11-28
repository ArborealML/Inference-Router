---
id: TASK-122
title: Admit requests from either ingress into one fleet queue
status: To Do
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-18T20:32:00Z'
updated_date: '2026-11-28T13:13:00Z'
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
One router writer owns the fleet queue and assignment. Additional ingress forwards to the same authority; no second pending queue, automatic writer takeover or distributed consensus. External deployment establishes singleton operation. Use fixed configured host/model placement first.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Send concurrent requests through two ingress routers. Each accepted request has one queue entry, at most one active delivery claim and one terminal result.
- [ ] #2 Exercise disconnection during claim handoff. After uncertain upstream acceptance, a successor records one indeterminate terminal result and sends zero additional inference requests.
- [ ] #3 Test fleet capacity, cancellation and the no-option deadline through both ingress endpoints, including an unreachable host.
- [ ] #4 Both ingress paths preserve policy, ticket, sequence and limits; the forwarder never owns independent pending work.
- [ ] #5 A slow/refused or unreachable peer cannot block another admissible provider; exact one-time reservation and unknown-acceptance fixtures prove isolation.
- [ ] #6 Forwarder authenticates to the writer and preserves verified caller scope, ticket cancellation and deadlines; no caller-supplied header grants owner or manager authority.
- [ ] #7 Duplicate start or stale router incarnation cannot submit under current authority; after lost ingress acknowledgment the forwarder never replays an ambiguously accepted POST as fresh work.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Use the shipping server entry point for an ingress-only role with one configured writer. Start no scheduler, engine probes or host operations on a forwarder. Authenticate the writer and ingress independently of caller identity. Have the writer verify the caller and derive owner/group scope with the existing private ingress rules. Reject a stale writer incarnation before admission. Preserve ordinary /v1 wire behavior and expose only the caller-scoped ticket operations needed by the client.

Relay each original request once, with its remaining deadline and cancellation. Disable redirects, transparent resend and fallback. Distinguish refusal before acceptance from lost acknowledgment after possible acceptance. The writer owns the sole ticket, terminal and physical retirement. Specify what a reconnect can observe for a known ticket and what the caller receives when its ID was lost. Do not infer upstream retirement from relay disconnect.

Run separate loopback processes with two ingress paths, one writer and fixed providers. Count admissions, upstream writes and terminals through both paths. Hold one provider, cancel before and after acceptance, break the relay before ticket headers and mid-stream, and attempt caller-scope spoofing and stale authority. Verify another eligible request progresses, explicit deadlines hold end to end, and retries never create another claim. Live two-host activation is qualified separately.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Fixture fixed placement can use explicit finite request deadlines; TASK-51's default deadline is a live cluster release gate, not a prerequisite to building forwarding. No new identity service.

The authenticated ingress plan is in independent review. Delivery can implement the forwarding process against a controlled writer while managed peer activation is qualified separately.
<!-- SECTION:NOTES:END -->
