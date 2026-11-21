---
id: TASK-72
title: >-
  Drain the waiting queue by age-weighted value before switching, without
  ping-pong
status: Waiting
assignee:
  - '@gpt-6.2-sol-scheduling'
created_date: '2026-11-18T01:22:00Z'
updated_date: '2026-11-21T04:13:00Z'
labels: []
milestone: m-5
dependencies:
  - TASK-69
  - TASK-25
  - TASK-51
  - TASK-71
priority: high
ordinal: 72000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wake after the first physical queue-driven model switch. The continuing-arrivals experiment has an approved plan. Its timing values are experimental inputs. Implementation and measurements are parked.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Under continuing arrivals for A and B, count switches and completed cohorts. A bounded-load test respects the SPEC switch limit while serving both models repeatedly.
- [ ] #2 With bounded inference durations and continuing B arrivals inside capacity, an aged eligible A completes within the SPEC scheduling bound. A separate overload test verifies bounded queue expiry and its event.
- [ ] #3 For eligible hosts, batching uses measured transition cost and hysteresis while the maximum-wait bound still protects aged requests. Demand and age cannot override a host refusal.
- [ ] #4 With B resident and eight B tickets plus one equally aged A ticket queued before dispatch, all eight B tickets finish before the switch to A. Record one B-to-A transition and successful A completion.
- [ ] #5 Repeat the fixed cohort with A initially resident. With the batch selected before dispatch, verify A-to-B then B-to-A, eight successful B completions before A, and no intervening switch.
- [ ] #6 Concurrent independent Pi sessions complete same-model and mixed-model requests through one AIR queue. Correlate each session’s streamed replies, model, ticket order and observed transitions.
- [ ] #7 Record per-engine/model switch duration, prompt tokens actually processed, prefill time and supported cache reuse, alongside queue wait and time to first output. Mark unavailable observations explicitly.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Exercise two independent sessions on the same warm model, then different models with overlapping submissions. Retain session affinity where possible. Compare switch counts, queue waits and time to first output for the same arrivals. Use measured swap and prefill costs to choose eligible batches while preserving bounded wait. Separate router timing and backend observations from estimates. Reuse the first primary-host swap and Pi launcher work.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Review the built-in decision through real queue admission, exact take, readiness and send on one simulated native engine with four lanes. Cover both initial residences, stable per-session identities, a held stream, an inclusive age boundary and a separate expired request. Changing only measured switch history must change the observed launch. Prompt-character and prefix estimates do not establish measured prefill speed or cache reuse. Review requires trained and unknown switch costs, fixed batch membership, and incumbent recovery after mutation-free refusal. Live drain, enrollment, default expiry and sustained switch-rate proof remain open.

The staged launcher preserved distinct session identities through ordinary AIR. In the fixed distinct-thread scheduling fixture, retiring the last physical HTTP use wakes the waiting alternate. All three offline deadline cases passed at e13ba1f. The corrected fixture observes disposal of the claimed timer while its callback is held and bounds caller cancellation. After release the handler runs once, with one terminal outcome and no second wake or rearm. Independent review closed both findings. Actual host prefill and sustained switch rate remain unmeasured.
<!-- SECTION:NOTES:END -->
