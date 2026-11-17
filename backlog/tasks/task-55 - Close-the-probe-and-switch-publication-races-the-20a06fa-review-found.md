---
id: TASK-55
title: Close the probe and switch publication races the 20a06fa review found
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T20:41:00Z'
labels:
  - concurrency
  - scheduling
dependencies: []
priority: high
ordinal: 55000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The deep review of 20a06fa returned REVISE; the coding session verified all six findings and accepted none as a gap. Four are
publication honesty in Engines/EngineRuntime.cs, and share one failure shape: a write that publishes state a reader was not
entitled to see. (1) HealthLoop awaits the model fetch and then publishes (:213-214); the compare-and-swap keeps the switch
marker but not the list, so a switch's fresher list is replaced by an older fetch (:144-151). (2) **Marker and switch state
are ordered wrongly at both ends.** :457 installs the switch state and :458 publishes the marker, so a dispatch pass landing
between the two sees an engine claimed for a model load but not marked switching, and can dispatch into it; EndSwitch
(:551-558) has the mirror-image order and does not hold _switchLock. A plan review falsified the fix first written here —
**swapping the two EndSwitch statements is the wrong fix**, because the scheduler's Switching field comes from the snapshot
marker (RouterCore.cs:462), so clearing the marker first makes a mid-switch engine look not-switching. The shape is a short
await-free **transition gate** over both transitions: on start, publish the marker then install the switch state; on end,
write idle state then clear the marker; readiness task resolution stays outside the gate; lock order is switch-lock then
gate, never the reverse, since EndSwitch is already called under the switch lock (:472). (3) A wait_health=false switch whose
model refresh fails reports completion on a stale list (:629-644). (4) Every ordinary poll bumps the capability version
because the fetched array is always a new instance, so the scan's stability check never settles and the refusal 20a06fa
shipped rarely fires.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 a probe publication landing during a switch is gated: the switch's list is never replaced by an older fetch
- [ ] #2 a barrier test shows no dispatch pass can enter an engine between claiming it for a switch and publishing the marker
- [ ] #3 a probe begun during a lifecycle mutation publishes neither Healthy=true nor a model list; restoration comes from the switch's own proof or a post-switch probe
- [ ] #4 two identical successive probes leave the capability version unchanged, so the scan's stability check settles
- [ ] #5 every uncertain switch exit (failed stop, failed launch, readiness timeout, unverified completion, failed refresh) publishes health-only, never a captured list
- [ ] #6 SPEC 5.5 and a revision-log line state the gate, the publication rule, the unknown-on-uncertain-exit posture, and the fence's limit
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**Plan review, first pass, changed the design.** It falsified the marker-ordering fix this task first carried, and the coding
session confirmed the falsification in the code (RouterCore.cs:462 reads Switching from `cap.SwitchingToward`, not from the
engine's own `_switchState`). A second plan pass on the corrected gate design is dispatched; the task stays To Do until code
moves.

**Accepted gap, to be recorded in SPEC 5.5 rather than fixed here:** the version fence orders publications, not reality. An
engine restarted outside the router, with nothing in the probe payload showing the discontinuity, evades it.

**Order of work inside this task:** transition gate, publication fence, health-only failure publish, unknown-on-uncertain-exit;
then quiet-poll version stability (#4). TASK-57 and TASK-56 follow, and TASK-54's remaining half follows all three because it
acts on the refusal predicate and needs it accurate.
<!-- SECTION:NOTES:END -->
