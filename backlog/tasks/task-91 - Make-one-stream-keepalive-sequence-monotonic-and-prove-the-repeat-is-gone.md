---
id: TASK-91
title: Make one stream keepalive sequence monotonic, and prove the repeat is gone
status: Waiting
assignee:
  - '@codex-6.2-sol'
created_date: '2026-11-18T07:51:00Z'
updated_date: '2026-11-18T09:06:00Z'
labels:
  - wire
  - stream
dependencies: []
priority: medium
ordinal: 90000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Measured live at 7d01520, twice, through the router fronting one OpenAI-shaped loopback upstream with auto_switch off: every streamed completion emits three keepalive frames — seq=0 status="queued model=…", then seq=1, then seq=0 status="queued model=…" again. SPEC 6 documents one keepalive sequence per stream, so the third frame is not a legal continuation: a client that reads seq for liveness, or drops duplicates by seq, sees a stream that stalled and restarted, and the repeat also lets an earlier phase restate its status. Reproduction: POST /v1/chat/completions with stream true against a running ServerApp that spends one queued phase. Source shows only two keepalive call sites sharing one counter (ServerApp.cs:191-208), so the third frame is not explained by the visible code: locate the writer before proposing a fix.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A live-ServerApp test reproduces the repeated keepalive sequence number on one stream, deterministically, with no sleeps.
- [ ] #2 SPEC 6 states what seq means, and a pin asserts one stream keeps it monotonic with no repeats.
- [ ] #3 Every other byte of the stream is unchanged, proven with the same frame-identity assertion as TASK-82.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Provenance: first defect this seat measured off a live router rather than a test. At 07:52Z the already-built Debug server was run with an untracked front-only probe config (remote engine, no lifecycle block, auto_switch off) against an already-running OpenAI-shaped fake backend on a loopback port; no engine was started or stopped, and the router bound loopback only and was stopped again. Reproduction, identical in two runs: curl -N -X POST http://127.0.0.1:18099/v1/chat/completions -H "content-type: application/json" -d "{\"model\":\"bench-model\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"stream\":true}" | grep -n keepalive -> frames seq=0 status, seq=1, seq=0 status. One candidate mechanism, NOT verified: a rented frame buffer reaching the pipe twice, since the repeated frame is byte-identical to the first. Sequencing: [TASK-82](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-82%20-%20Prove-the-stream-relay-preserves-every-frame-when-the-upstream-splits-it-at-any-byte.md) asserts frame identity over the same relay, so these two land one at a time.

Second measurement at 08:05Z, same build, same request: an upstream that stalls nine seconds before its first byte produced only seq=0 and seq=1, then the chunks and the sentinel — no repeat, and no keepalive at all during the stall. So the repeat is timing-dependent, and the periodic tick is not the writer: every keepalive write precedes the channel drain. Delegated at 08:09Z to a delegated worker (gpt-6.2-sol, medium reasoning) in its own worktree on its own branch, tests only and no commits, because its sandbox cannot write the shared gitdir. Wake condition: its report, with raw command output, ten consecutive runs, and a mutation control.

Deliverable arrived 08:45Z, uncommitted in its worktree: a KeepaliveSequenceTests file in the test project. Wake condition met for reading, not for closing — the implementer seat reviews and commits it, and its branch was cut at 13fb79d, two code commits behind, so it rebases on d6cfb38 like the other worker. Report with raw output and mutation evidence is what the review reads against.
<!-- SECTION:NOTES:END -->
