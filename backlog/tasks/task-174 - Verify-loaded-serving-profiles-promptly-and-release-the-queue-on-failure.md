---
id: TASK-174
title: Verify loaded serving profiles promptly and release the queue on failure
status: Done
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-22T09:41:00Z'
updated_date: '2026-11-22T11:01:00Z'
labels: []
milestone: m-4
dependencies:
  - TASK-173
references:
  - src/Arboreal.InferenceRouter/Engines/EngineRuntime.cs
  - src/Arboreal.InferenceRouter/Scheduling/LocalLifecycleGroup.cs
priority: high
type: bug
ordinal: 169000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Qwen reported its expected context and ran with one sequence, but its models endpoint omitted slot capacity. Derive the managed vLLM launch argument from the selected profile. Validate the configured evidence source at load and reload. Reuse existing failure cleanup, quarantine and scheduler wakeup. Deliver after the Pi turn fix and verify them together.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 A matching managed vLLM profile streams using the reported model and context plus effective --max-num-seqs from the exact launched or adopted serving process. Existing HTTP slot gauges still work.
- [x] #2 Absent, invalid or ambiguous runtime evidence stays unknown. Once the requested model answers, verification lasts at most ten seconds or the remaining startup budget, whichever is shorter. Retries never reset the deadline.
- [x] #3 A confirmed mismatch fails immediately with the field and both values. Unknown evidence expires with the missing field named. Slow cold loads retain their startup allowance.
- [x] #4 For both mismatch and missing evidence, the affected ticket terminates, SwitchingToward clears and the next eligible queued request streams after Docker mutations settle. Preserve cancellation and failed-target retry protection.
- [x] #5 In a live Pi session, observe Hy4 remain resident through its final tool continuation and response. Observe the original follow-up drive the switch, stream on Qwen and verify its 262144-token context and one sequence.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Implement opt-in vLLM capacity verification for local Docker using the exact container ID, host cgroup membership, process start identity and bounded token-preserving argv reads. Derive the common launch slot argument from the named profile. Use the same verified capacity for launch, adoption and warm polling. Arm the residual verification deadline on the first matching model response. Settle failure through existing queue cleanup and test a waiting successor. Integrate after the Pi fix.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
In the installed Pi run, Hy4 kept its container and process generation through its final reply. The original queued Qwen follow-up drove the switch, streamed, and completed. Independent backend observations showed 262144 tokens and one sequence. [PR 81](https://github.com/ArborealML/Inference-Router/pull/81) passed 221 focused tests at f39dc13. Review confirmed the generation checks and identified timer ordering. Moving the response timer after readiness verification passed 145 affected tests at ee88bef. The integrated native executable accepted the installed config. The final PR passed hosted build, tests, native publish and smoke checks and landed after the Pi change.
<!-- SECTION:NOTES:END -->
