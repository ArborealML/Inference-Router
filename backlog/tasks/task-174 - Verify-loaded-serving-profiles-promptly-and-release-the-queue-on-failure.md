---
id: TASK-174
title: Verify loaded serving profiles promptly and release the queue on failure
status: To Do
assignee:
  - '@gpt-6.2-sol-lifecycle'
created_date: '2026-11-22T09:41:00Z'
updated_date: '2026-11-22T09:53:00Z'
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
- [ ] #1 A matching managed vLLM profile streams using the reported model and context plus effective --max-num-seqs from the exact launched or adopted serving process. Existing HTTP slot gauges still work.
- [ ] #2 Absent, invalid or ambiguous runtime evidence stays unknown. Once the requested model answers, verification lasts at most ten seconds or the remaining startup budget, whichever is shorter. Retries never reset the deadline.
- [ ] #3 A confirmed mismatch fails immediately with the field and both values. Unknown evidence expires with the missing field named. Slow cold loads retain their startup allowance.
- [ ] #4 For both mismatch and missing evidence, the affected ticket terminates, SwitchingToward clears and the next eligible queued request streams after Docker mutations settle. Preserve cancellation and failed-target retry protection.
- [ ] #5 In a live Pi session, observe Hy4 remain resident through its final tool continuation and response. Observe the original follow-up drive the switch, stream on Qwen and verify its 262144-token context and one sequence.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Independent review identified the need to reuse capacity verification on launch, adoption and warm reuse. A host observation confirmed the vLLM serving process is an immediate child of the container init, with one sequence argument. Bind token-preserving evidence to the exact running generation and avoid reacquiring the lifecycle operation lock during adoption.
<!-- SECTION:NOTES:END -->
