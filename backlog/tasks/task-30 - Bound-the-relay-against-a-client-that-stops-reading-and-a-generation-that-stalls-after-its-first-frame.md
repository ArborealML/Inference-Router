---
id: TASK-30
title: >-
  Bound the relay against a client that stops reading, and a generation that
  stalls after its first frame
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T07:01:00Z'
updated_date: '2026-11-17T08:14:00Z'
labels:
  - stream
  - transport
dependencies:
  - TASK-34
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
- [ ] #3 A generation that produces no byte for a stated idle period after its first frame ends in band, using the shape SPEC 6 already defines for a committed stream (one error frame plus [DONE], SPEC:696 and SPEC:796) - no new truncation format; the idle bound and the error code name come from TASK-34
- [ ] #4 SPEC 14.1 row 2 no longer lists this as open, and a test pins each of 1-3 without a real engine
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Cut from the second external review of 76374d6, dispositioned in v0.12.6: both halves were accepted as gaps in SPEC §15 rather than fixed in that commit, and this task is their carrier. A fifth criterion is likely, on the review's suggestion: a stream_idle_timeout_ms key naming the idle bound of criterion 3 — to be added when the work starts, once SPEC can state its default.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T08:14:00Z

---

Wire-shape question resolved rather than pre-decided, and the answer is smaller than it looked. Checked against the code and SPEC: the in-band ending for a stream whose 200 is already committed is shipped precedent, not a new surface - response_timeout_ms already ends a quiet engine mid-turn with one engine_timeout frame plus [DONE] (SPEC:696, SPEC:796, and the v0.12.7-era note at SPEC:222 that streaming ceilings are in-band by construction). So AC #3 now names SPEC 6 for the shape and TASK-34 for the numbers. TASK-34 asks you for three constants and one policy (idle bound default, cancel-or-let-finish, frames-or-bytes) and starts at @claire. Start the machinery now: relay bound, stall detection, tests against the fake - the constants are the only thing waiting on a person, and a two-minute answer should not stall a week of work.

---
<!-- COMMENTS:END -->
