---
id: TASK-34
title: >-
  Decision: what the caller sees when a generation stalls or a client stops
  reading
status: Done
assignee:
  - '@chatswarm'
created_date: '2026-11-17T08:10:00Z'
updated_date: '2026-11-17T11:08:00Z'
labels:
  - stream
  - transport
  - wire
dependencies: []
priority: medium
ordinal: 33000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->

**What this is actually asking, after checking what the router already does.** The coding session flagged TASK-30 as
pre-deciding a `/v1` wire change. Checked against the code and SPEC, that is half right, and the half that is wrong
makes this a smaller ask than it looked like.

**What is genuinely new, and needs your number rather than an agent's guess:**

**Answer format (one line):** `1 120000, 2 cancel, 3 frames` — any single answer can be replaced with your number
and the rest stands.

**What is *not* waiting on this:** the coding session has been told to start TASK-30's machinery now — the relay
bound, stall detection, the tests that pin each path — with the numbers filled in when this lands. Blocking the
implementation on the constants would have made a two-minute answer into a stalled week.

**Links**
- The task that implements it: [TASK-30] (https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-30%20-%20Bound-the-relay-against-a-client-that-stops-reading-and-a-generation-that-stalls-after-its-first-frame.md).
- The acceptance criterion this feeds: TASK-30 AC #3, now worded to name this task for the numbers and SPEC §6 for the shape.
- Existing timeouts to line the new one up with: `response_timeout_ms` (per-engine turn ceiling) and `switch_wait_timeout_ms`, both described at [SPEC §5.5 / §14.1](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md#L1329).

<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Answer in one line: the idle bound after the first frame in ms (recommend 120000, 0 disables), cancel or let-finish for a caller that stops reading, and frames or bytes for the relay bound
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->

Evidence and reasoning moved out of the description under the answer-first rule (decision-15), so the description stays answerable in one sitting:

**Do this (4 minutes).** Three constants and one policy, in one line: `1 120000`, `2 cancel`, `3 frames`. 1 = how many milliseconds a stream may stay silent after its first frame before the router ends it (0 disables); 2 = whether the router cancels the upstream engine request when the caller stops reading, or lets it finish into a buffer nobody drains; 3 = whether the buffer bound counts frames or bytes. Nothing in this ask changes what a caller sees on the wire - that shape is already shipped and pinned by tests - so there is no new wire decision here.

**Already shipped, so not on the menu:** how a committed stream ends when the upstream goes quiet. SPEC §6 already
says a stream whose 200 is committed fails **in band** — one `engine_timeout` error frame followed by `[DONE]`
([SPEC:696](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md#L696),
[SPEC:796](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md#L796), and the revision note that
streaming ceilings are in-band by construction at [SPEC:222](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md#L222)).
That shape is precedent, pinned by tests, not a new surface. So TASK-30 does not get to invent a truncation format,
and you do not have to approve one.

1. **An idle bound after the first frame.** Today, once a stream has produced its first frame, an engine that goes
   silent is waited on forever ([SPEC §14.1 row 2](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md#L1329)
   records this as the open half). The fix is a config value: `stream_idle_timeout_ms`, and the question is the
   default. **Recommend 120000 (2 minutes), with 0 meaning disabled** — matching how `response_timeout_ms` and
   `switch_wait_timeout_ms` already treat 0. Too low truncates a reasoning model mid-thought; too high holds an
   engine slot on a dead generation. Consequence of approving: a caller gets a truncated answer *with a reason*
   instead of hanging; consequence of refusing: only the pre-existing ceilings apply, and a wedged engine holds a
   slot until its own `response_timeout_ms` or a health probe notices.

2. **What happens to the generation when the caller stops reading.** The router currently buffers bytes for a
   client that is not consuming, without limit. Bounding the buffer is not optional; the decision is what the router
   does with the *upstream* once it can no longer deliver: **cancel the engine request** (recommended — releases the
   slot and stops the router paying for tokens nobody receives; the partial generation is thrown away), or **let the
   engine finish** (the answer is discarded, the slot is held, and the cost is paid twice). Consequence of cancel:
   an engine that ignores cancellation still runs on, and the router's own cancel path is already tested for the
   client-hang-up case.

3. **The buffer bound itself.** Recommend a count of buffered frames rather than a byte count — a frame count is
   what actually bounds memory on a token stream and it is easier to reason about in config — with a default in the
   low hundreds, plus a named error code on the feed for both stalls so an operator can tell "the engine stalled"
   from "the caller stalled". Ask: approve a frame-count bound, or insist on bytes.

Citations restated by section, not line, because the SPEC anchors in the original text went stale within hours: SPEC 6 (in-band ending for a committed stream), SPEC 14.1 row 2 (what is still open, and the shipped ceilings), and the pinning test RequestPhaseTimeoutTests.AStreamThatNeverSendsAFrameEndsInsideTheStreamBecauseTheStatusIsAlreadySent.

Precedent confirmed independently by the coding session after my check, so it is no longer a single-agent claim: the pinning test is RequestPhaseTimeoutTests.AStreamThatNeverSendsAFrameEndsInsideTheStreamBecauseTheStatusIsAlreadySent at tests/Arboreal.InferenceRouter.Tests/RequestPhaseTimeoutTests.cs:196, and SPEC's quiet-engine wording (ends engine_timeout, one frame plus [DONE]) sits in the revision log near line 209 today. TASK-34 therefore stays numbers-and-policy for the owner and TASK-30 does not move behind a wire decision.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T10:58:00Z

---

Reassigned under decision-16. The three answers wanted: the idle bound after the first frame in ms (0 disables), what happens to a generation that stalls — cancel upstream and close, or let it run to its own ceiling — and what a client that stops reading gets. Constraint to hand the reviewer: the surface is `/v1`, OpenAI-compatible, so the shape is likely a truncation signal rather than a new error body, and SPEC §6 casing rules are frozen. The coding session is already building TASK-30's machinery — relay bound, stall detection, tests — with the numbers to be filled in when this lands.

---

created: 2026-11-17T11:02:00Z

---

Dispatched to the external reasoning service on 2026-11-17 at 11:02Z, with no repository access, and given the facts it needs: response_timeout_ms default 600000 applies only before the first frame; the ceiling deliberately ends at the first frame; a committed stream fails in band with one error frame plus [DONE]; a caller abort reports cancelled, never a timeout; both the stalled-reader buffering and the post-first-frame silence are currently unbounded. Six questions: the default idle bound in ms and its unit; cancel upstream or detach; the slow-consumer bound and whether it counts frames or bytes; what mainstream OpenAI client libraries do with an in-band error frame versus a missing [DONE]; the ways a bound makes things worse; whether one knob beats two.

Wake: that answer landing. The coding session implements TASK-30 meanwhile, with the numbers filled in after — the shape is already pinned by TASK-30 AC #3 (one error frame plus [DONE], no new truncation format).

---

created: 2026-11-17T11:08:00Z

---

Answer, under decision-16, adopted with its caveats kept attached: **1 `stream_idle_timeout_ms = 300000`, per engine, 0 disables; 2 on expiry cancel upstream and tear down, release the lease exactly once, and never claim the backend stopped unless that is established; 3 `stream_buffer_limit_bytes = 262144`, global, counted in bytes not frames, with abort plus a `slow_consumer` feed event at high water.** A fixed 30 s pending-write no-progress watchdog is a safety rule inside the relay, not a third config key. Two independent knobs, deliberately not derived from `response_timeout_ms`, because a wall-clock deadline kills productive long streams and one shared activity timer lets a busy upstream hide a blocked downstream.

Wake condition the answer sets for shipping the defaults: two gates, not a config PR — per-engine gap replay (measured gap histograms, 10,000 representative successful reasoning/tool/batched streams with zero false expiries, plus 299999/300001 ms injections and timeout-versus-byte races) and a slow-reader soak (stopped reader, one-byte blocked flush, >256 KiB frames, HTTP/1.1 and HTTP/2, slot saturation) showing bounded backlog and no leaks.

One premise this task asserted, flagged by the reviewer and NOT settled here: "the router buffers that caller's tokens without limit" may be wrong in the direction of better — Kestrel and TCP may already backpressure. TASK-30 measures that before it writes the number into SPEC. The caller-visible shape stays as SPEC §6 already defines it: one in-band error frame plus `[DONE]`, status already 200, and no new truncation format.

---
<!-- COMMENTS:END -->
