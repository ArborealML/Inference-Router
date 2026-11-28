---
id: TASK-122
title: Admit requests from either ingress into one fleet queue
status: To Do
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-18T20:32:00Z'
updated_date: '2026-11-28T13:28:00Z'
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
Select the role before constructing RouterCore. Run one stock Native AOT writer with direct ingress and one ingress-only process. Reuse verified caller scope for preprovisioned groups on both paths. Authenticate the ingress and writer independently with mutual TLS and fence their incarnations externally. Verify the caller at the writer, strip caller-supplied authority assertions and restrict forwarding to inference, discovery and scoped ticket status, events and cancellation. A forwarder starts no queue, probes or host operations.

Start finite budgets on first ingress before upload. Bind expiry to the authenticated request and clamp it at the writer using a verified clock-skew bound and monotonic watchdog. Bound concurrent relays, buffers and stalled writes. Send each admitted original once over a fresh direct HTTP/1.1 connection. Disable redirects, automatic negotiation, ambient authentication and retries. Check actual POST counts under connection loss. A downstream writer fault cancels upstream; physical retirement remains with its host.

Qualify a running writer first. When the acknowledgment is lost, report uncertain acceptance. A known ticket allows scoped observation and cancellation. For writer restart, persist acceptance before acknowledgment and possible use before dispatch. Recover the original operation under fenced authority, resolve an indeterminate result once, retain uncertain occupancy and never replay. Reuse the manager disposition contract. New client POSTs are distinct.

Count queue entries, claims, upstream POSTs, terminals and releases from both ingress paths against fixed loopback providers. Exercise forged scope, stale incarnation, refused or lost acceptance, disconnect, stalled writes, restart and one occupied provider in each direction. Qualify the default deadline and actual two-host placement at their respective release gates.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Independent review at 58b2f4d found that ordinary ticket headers do not prove acceptance, the writer ticket index is volatile, and one HttpClient call can retry internally. Exercise the published runtime with real connections. TASK-51 owns the no-option deadline. TASK-124 owns deployment qualification.
<!-- SECTION:NOTES:END -->
