---
id: TASK-91
title: Make one stream keepalive sequence monotonic, and prove the repeat is gone
status: To Do
assignee:
  - '@gpt-6.2-sol-stream-evidence'
created_date: '2026-11-18T07:51:00Z'
updated_date: '2026-11-20T04:18:00Z'
labels:
  - wire
  - stream
milestone: m-1
dependencies: []
priority: medium
ordinal: 90000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Historical fast-response keepalive duplication still lacks a matching reproduction. The controlled landed sequence and byte-identity evidence were audited; further replay is deferred behind stalled-reader work.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A live-ServerApp test reproduces the repeated keepalive sequence number on one stream, deterministically, with no sleeps.
- [ ] #2 SPEC 6 states what seq means, and a pin asserts one stream keeps it monotonic with no repeats.
- [ ] #3 Every other byte of the stream is unchanged, proven with the same frame-identity assertion as TASK-82.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Provenance: the first defect this seat measured off a live router rather than a test. At 07:52Z the built Debug server, a front-only probe config (remote engine, no lifecycle block, `auto_switch` off) and a scripted streaming upstream gave `seq=0,1,0` on one stream. At 08:05Z the same build against an upstream stalling nine seconds before its first byte gave only `seq=0` and `seq=1`, with no keepalive during the stall. The trigger therefore looks like a **fast** upstream rather than the stall tick, and that is the axis a reproduction has to vary. A 253-line `KeepaliveSequenceTests` arrived from a delegated worker; the coding seat reviewed it and the review is recorded below rather than reproducing the diff.

The coding seat rejected the worker's 253-line `KeepaliveSequenceTests` against AC #1: it passes against unmodified code, and its own log records that the live loopback repeat was never reproduced (the sandbox denied sockets); the only red comes from a mutation that replays frame 0 before the relay drains. Committing it would pin monotonicity without proving the defect is gone. **Go** given for a live reproduction on loopback: front-only config in the safe-smoke shape (remote engines, no lifecycle commands), a scripted streaming upstream, ten consecutive runs with a fast and a stalled upstream alike. A run that fails to reproduce is a measurement to record, not a pass. Until AC #1 holds, the file stays out of `main`.
<!-- SECTION:NOTES:END -->
