---
id: TASK-190
title: >-
  Survive an engine transition: drain before teardown, and never splice a
  continuation
status: Preparing
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-25T18:19:00Z'
updated_date: '2026-11-25T18:38:00Z'
labels: []
dependencies: []
priority: high
ordinal: 182000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
When the router tears an engine down while requests are in flight, the client connection simply ends. The owner's question is whether AIR should keep the connection open and resume underneath, or whether that belongs to the layer above that owns the transcript. Position so far: a true resume is impossible, because the engine's key-value cache and sampling state die with the process and tokens already sent cannot be un-sent. What is achievable is (a) do not tear down in-flight work in the first place, and (b) where nothing has been emitted yet, hold the connection open with keepalives and re-issue once against the new engine. Anything past that belongs to the caller, and AIR's obligation is to report the truth: a named reason, what was emitted, and the transition state. A design pass is dispatched to the external reasoning service on 2026-11-25.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A tier resize does not kill in-flight streams: it drains them, or refuses, and says which.
- [ ] #2 A stream with nothing emitted is retried once, within its deadline, against the successor engine, with the client connection held by keepalives.
- [ ] #3 A stream that already emitted tokens is never re-run: it ends in band with a named reason.
- [ ] #4 The named reason distinguishes a producer transition from a context limit from a stalled generation.
- [ ] #5 A client that stops reading and an engine that never returns are both bounded.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Design pass landed 2026-11-25 (external, top reasoning tier). Accepted refinements, verified against this code's behaviour before accepting: the safe boundary for any invisible recovery is NOT before the first token but before any attempt to write producer payload to the client — role, identifier and tool-call deltas and partial bytes all count, and a write that throws may still have delivered bytes, so the router must report write-attempt and completed-byte counters rather than tokens. After any possible exposure, never regenerate, skip, or splice: fail that attempt and let the caller keep what it has. Automatic transparent retry is deferred and initially off: it charges a full cold re-prefill, needs the old attempt fenced as dead, one attempt only, and the caller's original deadline and priority preserved rather than a fresh queue position. Resize sequencing accepted: reserve the switch's own ticket first, serve a finite cohort of already-compatible waiters, close dispatch atomically, and stop only when no old-epoch work remains; if the drain expires, defer or refuse the resize while healthy work is running, with forced preemption a separate explicit policy. The layering answer is a named handoff, not a resume: AIR reports cause and disposition separately — what it attempted, how much payload it wrote, whether it saw a completion marker, and the transition state — so the caller decides; the new refusal and failure names must go through wire review before shipping. Explicitly rejected: token splicing, AIR owning transcript or tool-effect recovery, full buffering, and indefinite waits.
<!-- SECTION:NOTES:END -->
