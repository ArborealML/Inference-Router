---
id: TASK-30
title: >-
  Bound the relay against a client that stops reading, and a generation that
  stalls after its first frame
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T07:01:00Z'
updated_date: '2026-11-17T16:01:00Z'
labels:
  - stream
  - transport
dependencies:
  - TASK-34
priority: high
ordinal: 29000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Two halves of one gap, both on the response path after the first byte. (1) The stream channel is unbounded, so a caller that keeps the TCP connection open but stops reading does not stall the pump — it makes the router buffer that caller's tokens without limit while the engine slot stays held. (2) The response ceiling ends at the first frame on purpose, so an engine that sends one byte and then goes silent holds a slot and a connection indefinitely. Both are pre-existing, not new in v0.12.5: the v0.12.5 work made the first-frame boundary explicit, which is why they are now nameable. Neither is fixed in v0.12.6 because each needs a wire decision — a new error surface for a truncated generation, and a policy choice about whether the router or the caller owns a slow consumer. The review also suggested a stream_idle_timeout_ms key; not filed as a criterion yet because the key does not exist and SPEC would have to state its default — see the task note.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A stream whose downstream reader stops consuming ends within a stated bound, with the engine slot released and a named error code on the feed
- [ ] #2 Buffering held for one stalled reader is bounded by a number that is in config, not a constant, and the bound's default is stated in SPEC - the number and whether it counts frames or bytes come from TASK-34
- [ ] #3 A generation silent past the stated idle bound after its first frame ends in band with SPEC 6's committed-stream shape: one error frame plus `[DONE]`, no new truncation format
- [ ] #4 The bound and its code name come from TASK-34's adopted values; the behaviour is pinned by `RequestPhaseTimeoutTests` and named in the SPEC 14.1 row
- [ ] #5 SPEC 14.1 row 2 no longer lists this as open, and a test pins each of 1-3 without a real engine
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Cut from the second external review of 76374d6, dispositioned in v0.12.6: both halves were accepted as gaps in SPEC §15 rather than fixed in that commit, and this task is their carrier. A fifth criterion is likely, on the review's suggestion: a stream_idle_timeout_ms key naming the idle bound of criterion 3 — to be added when the work starts, once SPEC can state its default.

Precedent confirmed independently by the coding session after my check, so it is no longer a single-agent claim: the pinning test is RequestPhaseTimeoutTests.AStreamThatNeverSendsAFrameEndsInsideTheStreamBecauseTheStatusIsAlreadySent at tests/Arboreal.InferenceRouter.Tests/RequestPhaseTimeoutTests.cs:196, and SPEC's quiet-engine wording (ends engine_timeout, one frame plus [DONE]) sits in the revision log near line 209 today. TASK-34 therefore stays numbers-and-policy for the owner and TASK-30 does not move behind a wire decision.

Superseded log entries for this task: [board trim, 2026-11-17](https://github.com/ArborealML/Inference-Router/blob/main/backlog/archive/board-trim-2026-11-17.md).
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->

created: 2026-11-17T11:08:00Z

---

Numbers and policy are decided (TASK-34 answered; the external answer was adopted because it contradicts nothing measured here). Ship defaults: `stream_idle_timeout_ms = 300000` **per engine**, `0` disables, measured on **upstream byte arrival** — reset by upstream bytes, never by keepalives or downstream writes, and bytes in hand beat an expiry race. `stream_buffer_limit_bytes = 262144` **global**, counted in bytes rather than frames, because frames vary in size. On idle expiry: cancel upstream, release the lease exactly once in a `finally`, and record `upstream_cancel_requested` always and `upstream_stop_confirmed` only when established — otherwise the feed says `backend_execution_unknown`, because cancelling an HTTP request does not guarantee GPU compute stopped. At high water with no drain: cancel upstream, close or reset downstream, release the lease, emit `slow_consumer`. Caller-visible shape stays exactly as SPEC §6 has it: one in-band error frame plus `[DONE]` on a stream that is still writable, close without ceremony when it is not, no new truncation format. A caller abort stays `cancelled`, never a timeout. The 30 s pending-write no-progress watchdog lives inside the relay as a safety rule, not a config key.

**Do not land the defaults as a bare config PR.** Two gates come first, and this task is not Done without them: per-engine gap replay (measured gap histograms; ~10,000 representative successful reasoning/tool/batched streams with zero false expiries; injections at 299999 and 300001 ms; timeout-versus-byte races; an upstream that ignores cancellation) and a slow-reader soak (stopped reader, one-byte blocked flush, frames larger than the cap, HTTP/1.1 and HTTP/2, abort and slot saturation) showing bounded backlog, bounded RSS, expiry at high water, and no leaks.

**Measure the premise before writing it into SPEC.** This task's Description says the router buffers a stalled reader without limit. That was written from reasoning, not measurement, and Kestrel plus TCP may already apply backpressure; AC #2's number and SPEC's wording should say what the code actually does, with the test that shows it.

---

<!-- COMMENTS:END -->
