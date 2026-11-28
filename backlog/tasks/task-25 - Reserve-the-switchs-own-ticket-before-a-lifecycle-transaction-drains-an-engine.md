---
id: TASK-25
title: >-
  Reserve the switch's own ticket before a lifecycle transaction drains an
  engine
status: Waiting
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-17T02:41:00Z'
updated_date: '2026-11-28T13:06:36Z'
labels:
  - engine
  - lifecycle
milestone: m-4
dependencies:
  - TASK-206
priority: high
ordinal: 25000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wake when TASK-118 supplies executable authority. The manager interface and exact recipe identity are on main. Wire ordinary startup, exact physical draining and the existing instance-bound sender into one acceptance run shared with TASK-118. The router owns the queue and the selected ticket. The host manager owns physical execution. Live commissioning requires its existing external authority.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 A switch request is distinguishable from ordinary inflight work and is excluded from the drain count
- [x] #2 Admission to the engine is closed for the duration of the transaction, and a request aimed at it is queued rather than admitted
- [x] #3 Existing inflight requests finish before anything is stopped, and the drain has a timeout with an actionable outcome
- [x] #4 Queue counters and terminal events still agree with what actually happened to each ticket
- [ ] #5 Pause a waiter after Ready and begin a competing switch. Its request reaches only the instance its reservation authorizes, or returns to admission before any upstream write.
- [x] #6 Hold an upstream send past caller timeout. Verify one caller terminal, a released logical slot, and zero stop or rebind operations through drain expiry. After late-response disposal, a fresh authorized drain can proceed.
- [ ] #7 One paired production-composition fixture pins ordinary enrollment, exact recipe/body/generation, rebind before first write, and late retirement; neither component claims production closure without the same pinned witness.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Build one normal startup factory around the established enrollment, issuer and staging interfaces. Require authentic scope for each enrolled group. Check mixed local and enrolled membership for conflicting physical ownership. Reuse the existing verified sender and retain the plain local startup path.

Reserve the original ticket once. Capture its frozen recipe, enrollment, resource epoch, full incumbent process and per-incarnation usage tracker before closing admission and the issuer together. Count each authorized write through conclusive exchange retirement and preparation cleanup. Retire older preparations outside the gates. Preserve existing admission, router and queue lock ordering. Keep waits and callbacks outside locks.

Use one monotonic drain budget. Before stop, recheck the captured transition, incarnation, tracker, issuer cutoff and reservation epoch under authority, then persist stop intent for that exact target. Missing evidence, timeout or uncertain restart retains exclusion. Late retirement permits fresh authorization. A stale finalizer cannot clear a successor.

Through ordinary server construction, capture the original queued ticket and exact upstream bytes. Test busy drain, rebind before first write, downstream departure, held physical retirement, lost acknowledgment, restart, independent-domain progress and local serving. Count stops, writes and terminals. TASK-190 owns durable reconciliation, TASK-197 conformance and TASK-198 quarantine.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The review found that the current admission check rejects a busy incumbent and that lifecycle staging closes before drain. The historical drain also treated an absent tracker as zero. Capture the original tracker and seal before discarding staging. Recheck its authoritative zero and the exact serving process before any mutation. TASK-25 and TASK-118 consume the same production-composition evidence.

[Host-manager drain](https://github.com/ArborealML/Inference-Router/pull/99) at d84dccc passed 346 focused tests. HTTP fixtures supply controlled authority. Disconnection preserved physical ownership. At aef5e2a, 77 checks qualified selected credit and legacy paths. Review at 5e163f5 found an earlier heartbeat could stall across selection. At 1ad5cea, 126 stream checks passed. A synchronous terminal result could still cross selection. At a474bdf, 134 checks passed, including synchronous success and terminal refusal. [Merged CI](https://github.com/ArborealML/Inference-Router/actions/runs/46147021365) passed 2,448 tests and Native AOT. [Combined private routing](https://github.com/ArborealML/Inference-Router/commit/8b74ffbfecd950a8da444544ac009e1d68ad55cb) passed 130 stream, dispatch and scope checks.
<!-- SECTION:NOTES:END -->
