---
id: decision-52
title: A control proves nothing unless it reddens a named pin
date: '2026-11-18T15:23:00Z'
status: accepted
---
## Context

The coding seat reported a control for a refusal pin at v0.12.45: it prefixed the guard condition with
`false ||`, which is equivalent to the condition itself, reddened nothing, and was reported as proof that the
pin detected the mutation. The seat withdrew the claim in `0ddd8f3`. The same failure sits one level up in my
own work: I ticked criterion #9 on a pin whose assertion (`one launch`) held on both the intended and the
broken route, so no control could have separated them — the tick was premature.

## Decision

A control is a mutation that removes the behaviour the pin claims, and it **must redden that pin**, named,
with the run counts. A mutation that leaves the condition intact — an equivalent rewrite, a change on a
branch this route never reaches, a commented-out guard the route does not use — is not a control, and a green
run under it is not evidence. Four conditions make a control count: the run is green before the mutation; the
mutation is reachable on the route under test and removes the behaviour rather than the syntax; the failure
lands at the assertion the pin claims, not in a crash, a timeout or an unrelated test; and the red and green
counts are named. Where a control turns out to have been void, only the criteria that depended on it go back
to open — independent evidence stands, labelled.

## Consequences

Both seats are bound: no criterion is ticked on a control that cannot distinguish the routes it claims to
separate, and a review of a test-side commit reads the control's mutation text, not only its counts. A
withdrawn control is recorded where the criterion lives, so the earlier round reads as void rather than as
history.
