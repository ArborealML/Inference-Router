---
## Context

TASK-61 re-authorises a waiting caller when it resumes, so that the wire answer and the feed cannot
disagree about a switch that never happened. Landed at 042e212 (v0.12.34), main went red: CI run
44241484309 failed `SwitchFairnessTests.AWaiterBehindAnotherProfilesSwitchTakesItsOwnTurn`, and the
board reproduced both that failure (2 of 4 runs) and a failure of the row's own new pin
(`AWaiterOvertakenAtTheResumeReadTakesItsOwnTurnInsteadOfAnAnswerItCannotSupport`, 1 of 2 runs). The
second failure's diagnostic names four launches — `extra, m, extra, m` — with a switch still in flight
at the caller's 25 s ceiling. `ReadyIn` answers false while `SwitchingToward` is set, so a caller whose
own switch had *proved* its target read "not authorised", fell through, claimed again, and drove the
other caller into the same fall-through: two callers with different targets on a one-model engine
alternating lifecycle transactions until someone gives up.

## Decision

A proof a switch published for its own target stays valid for the caller that ran it. A switch marker
raised by a **later** claim is not a contradiction of that proof, and must not send its holder back
through the claim path. A waiter re-decides only on an observation that actually contradicts its target:
health withdrawn, the model gone from the reported list, or a window that no longer fits. And a
re-decide that would unload a target another caller has just proved is not free: that authority is the
lifecycle reservation (TASK-25) read against the age-weighted drain (TASK-72), and neither exists yet.
Until both do, the residual window — a successor claims between another caller's proof and its own
dispatch — is stated in SPEC §15 as an accepted gap with those two rows as its owners, not closed by a
fall-through that ping-pongs.

## Consequences

TASK-61 keeps the part that is safe: a switch that ended false is `Failed` **once** (no internal retry,
so a caller with no ceiling cannot relaunch forever), and a caller is never told `switch to X failed`
for a switch that succeeded. Its AC #4 pin is open again, because the pin passed on thread scheduling —
it failed one run in two under the filter — which is exactly what that criterion promises. The row gains
a criterion that two callers wanting different models on a one-model engine produce no alternating
launch sequence, asserted as a launch sequence rather than as the absence of a timeout. TASK-61 now
depends on TASK-25 and TASK-72 for the half it cannot close, and TASK-68's waiter criterion is worded
the same way so the gauge work cannot re-introduce this failure.

## Alternatives

Revert 042e212 (rejected: it fixes a real wire-versus-feed contradiction, and history is not rewritten).
Bound the ping-pong with a re-decide counter (rejected: a counter makes the alternation expensive rather
than wrong-then-right, and leaves two callers paying lifecycle cost for each other). Land the drain and
the reservation first (rejected as sequencing: main is red now, and the safe half of the fix is what
makes it green).
