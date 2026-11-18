---
id: TASK-55
title: Close the probe and switch publication races the 20a06fa review found
status: Done
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T20:41:00Z'
updated_date: '2026-11-18T19:29:00Z'
labels:
  - concurrency
  - scheduling
milestone: m-2
dependencies: []
priority: high
ordinal: 55000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Four external reviews of 20a06fa returned REVISE; each finding was verified in the code, none a gap.
One invariant covers them: **a capability snapshot is either current-and-verified or unknown** — today it can be
current-and-assumed. Marker and switch state are ordered wrongly at both ends: EngineRuntime.cs:457 installs the switch state
and :458 publishes the marker, so a dispatch pass between them sees an engine claimed for a model load but not marked switching
and can send a request into the engine about to be stopped; EndSwitch (:551-558) has the mirror order without _switchLock.
Swapping those two statements is **not** the fix — the scheduler reads Switching from the snapshot marker (RouterCore.cs:462),
so clearing the marker first makes a mid-switch engine look idle. The shape is a short await-free **transition gate** over both
transitions: on start, publish marker and unknown together, then install the switch state; on end, clear the marker only while
it still names that switch, new starts excluded; readiness resolution stays outside the gate; lock order is switch-lock then
gate (:472). A probe begun during a mutation also publishes a stale-but-healthy claim, the failure publish at :228 writes a
list captured at call time, and every poll bumps the version, so the scan never settles and the shipped refusal rarely fires.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 a probe publication landing during a switch is gated: the switch's list is never replaced by an older one
- [x] #2 the start publishes marker and unknown together, then installs the switch state; a barrier test shows no dispatch pass enters in between
- [x] #3 the end clears the marker only while it still names that switch, under a gate that excludes new starts
- [x] #4 a probe begun during a lifecycle mutation publishes neither Healthy=true nor a model list; restoration comes from the switch's proof or a later probe
- [x] #5 two identical successive probes leave the capability version unchanged, so the scan settles
- [x] #6 every uncertain switch exit (failed stop or launch, readiness timeout, unverified completion, failed refresh) publishes health-only, never a captured list
- [x] #7 a switch whose health proof succeeded makes the engine eligible without another poll, list and health in one publication
- [x] #8 after a switch commits, a waiting queued request is considered again without a poll
- [x] #9 a readiness answer requires a healthy, marker-free capability holding the target with a fitting window
- [x] #10 SPEC 5.5 and a revision-log line state the gate, the publication rule, the unknown-on-uncertain-exit posture, and the accepted gaps
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**Landed at fd5e0cd (SPEC v0.12.28)**, ten tests in `CapabilityPublicationTests.cs`: marker-before-claim read at the instant the
claim installs (`TheMarkerGoesUpBeforeTheEngineIsClaimed`, through an internal seam null in production);
`AClaimedSwitchIsInvisibleToDispatchAndOutlivesAStaleEndOfTheLastOne`; `AProbeTakenBeforeASwitchCannotOverwriteWhatTheSwitchProved`;
`QuietPollsOfUnchangedContentLeaveTheGenerationAlone`; three tests for the uncertain exits;
`AProvenSwitchMakesTheEngineEligibleWithoutWaitingForAnotherPoll`;
`TheDispatchNudgeArrivesOnlyAfterTheCapabilityTransitionCommits`. Mutation evidence: removing the fence, the start-side unknown,
the marker-before-claim order, the EndSwitch successor guard, the quiet-poll content compare, or the dispatch hint each fails a
test. Suite 175 green; AOT publish clean with trim warnings on; CI green on main (run 44192523835).

**AC #9 accepted as indirect, by the board.** `IsReadyFor` implements healthy-and-marker-free, but no public path reaches the
marker clause now, since every marker state also carries Healthy=false; the window clause is covered by the escalation tests.
Pinning that residual, and the concurrent-claim exclusion whose race test was dropped as a flake (SPEC §15 records the drop), is
TASK-59 rather than a claim made here.

**Accepted gaps in SPEC §5.5/§15, not code:** the fence orders publications, not the engine, so a restart outside the router's
sight evades it; and the quiet-poll compare is content-wise, so order-only and duplicate-count churn bump the version. Both plan
reviews and each finding's disposition are in §15.

**Commit review of fd5e0cd: REVISE, five blocking findings, all five verified in the code, none accepted as a gap.** They are
TASK-61 (stale Ready at resume, the readiness fast path evaluated outside the switch lock, and the dispatch hint settled before
the readiness task), TASK-62 (a wait_health=false refresh published as proof whether or not it named the target, and a test at
CapabilityPublicationTests.cs:387-413 that pins the wrong posture), TASK-63 (the unfenced failure publish and the epoch-less
/props gauges), and TASK-64 (the scheduler pass that snapshots an engine a switch then claims — the admission/drain design, not
a patch). The review also confirmed what was right: claim CAS order, proof published under the marker, current-list preservation
at uncertain exits, the in-gate successor guard, the lock order with no inverse, and no ordinary deadlock.
<!-- SECTION:NOTES:END -->
