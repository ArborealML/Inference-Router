---
id: TASK-170
title: Run a repeatable live tuning workload for one engine and model
status: Ready for Review
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-22T09:21:00Z'
updated_date: '2026-11-28T11:52:00Z'
labels: []
milestone: m-14
dependencies:
  - TASK-149
  - TASK-146
references:
  - docs/hardware-tuning-design.md
  - src/Arboreal.InferenceRouter.Cli/AirTuneWorkloadPlan.cs
modified_files:
  - README.md
  - SPEC.md
  - docs/hardware-tuning-design.md
  - src/Arboreal.InferenceRouter.Cli/AirTunePromptFit.cs
  - src/Arboreal.InferenceRouter.Cli/AirTuneTextGenerator.cs
  - tests/Arboreal.InferenceRouter.Tests/AirTunePromptFitTests.cs
  - src/Arboreal.InferenceRouter.Cli/AirTuneCompare.cs
  - src/Arboreal.InferenceRouter.Cli/AirTuneFile.cs
  - src/Arboreal.InferenceRouter.Cli/AirTuneRecord.cs
  - src/Arboreal.InferenceRouter.Cli/AirTuneRun.cs
  - src/Arboreal.InferenceRouter.Cli/AirTuneRunEvidence.cs
  - src/Arboreal.InferenceRouter.Cli/AirTuneSingleModel.cs
  - src/Arboreal.InferenceRouter.Cli/AirTuneWorkloadPlan.cs
  - src/Arboreal.InferenceRouter.Cli/Program.cs
  - src/Arboreal.InferenceRouter.Cli/ShellCompletion.cs
  - src/Arboreal.InferenceRouter/Arboreal.InferenceRouter.csproj
  - src/Arboreal.InferenceRouter/SemanticStreamObserver.cs
  - tests/Arboreal.InferenceRouter.Tests/AirTuneSingleModelTests.cs
ordinal: 165000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Implement air tune run for one configured engine/model/exact recipe using the existing planner and comparator. Complete deterministic runner/measurement fixtures without live hardware access; TASK-154 owns authorized real reference measurements. Physical lanes and occupied concurrency are distinct.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Verify exact model/recipe, window, physical lanes, tokenizer/template, hardware and exclusive admission before samples; controlled fixtures include refusal.
- [ ] #2 Freeze deterministic workload, body digests, rendered counts and enforced output allowance; include reserve/cache occupancy. Failed preflight sends no workload.
- [ ] #3 Run near-window short waves and long-decode cells; measure overlap, generated length, TTFT, prefill/decode and end-to-end time; missing metrics remain unavailable.
- [ ] #4 Separate per-call bytes, cumulative bytes, calls and time for supported 1M-scale fitting; verify final rendered count and unclamped allowance.
- [ ] #5 Record each request/wave, occupied concurrency, semantic outcome/usage, failures, resources and physical settlement; timeout cannot prove vacancy.
- [ ] #6 Persist versioned immutable evidence, failures and inconclusive results; retain offline comparison compatibility without granting qualification.
- [ ] #7 Deterministic capture and synthetic measurement fixtures exercise exclusive admission, stop on unproved settlement, and bound workloads; actual hardware qualification belongs to the measurement workstream.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Resume the recovered fitter and its original regression deck. Reuse the measured prompt bytes. Search the full remaining record tail within the existing call budget and verify the returned body with the exact count; bounded search can report inconclusive. Qualify the two-bytes-per-token counterexample, exact counter controls, budget limits and rejection paths.

Use the same fitter, workload planner and stream observer in one run command. Freeze seed, configured engine/model/tier, measured count, body and output allowance. Admit only an exclusive verified resident; refuse before workload when a prerequisite is absent. Qualify warmup and repeated measured waves via the normal CLI under controlled capture. Preserve separate measurement, settlement, evidence and cold-load requirements. Count only authoritative completed work toward throughput. Missing usage remains unavailable. Use the shared semantic observer from TASK-187 and the single runner from decision-81 for comparison. Keep real hardware authorization and adopted mode measurements with their owning tasks.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The previously missing prompt-fitter source was recovered at 94a2186 on the original local branch. It includes a Maya count adapter, deterministic body builder and focused checks. Independent commit review identified a false refusal when eight guessed tail lengths all clamp to a full record; the original Release build rejected an attainable exact 32,128-token prompt in a deterministic example with two characters per token. Preserve the original source and existing review. The original plan review also requires traffic isolation, non-disruptive acquisition, exact rendered counts and independent native timing evidence. Earlier handoff: [initial fitter work](https://github.com/ArborealML/Inference-Router/commit/3c60c3cea522c6e1956e03d791fe178d4bd3bd15). The [recovered fitter and bounded search correction](https://github.com/ArborealML/Inference-Router/pull/96) passed eight focused Debug tests, including the formerly failing input, independent source review and hosted CI. The correction is on main. Keep prompt search bounded for nonmonotone counters and qualify count limits, failure and settlement through the runner. Full runner and live hardware qualification remain open.

[Single-model runner](https://github.com/ArborealML/Inference-Router/pull/98) at aeb2703 passed 146 focused tests. The candidate records independent served-output and decode-work counts, checks the hardware before and after each wave, and publishes one comparison-compatible artifact. Timeout with unconfirmed physical release withholds rates. Independent review and hosted CI are pending. Stock hardware runs require a production exclusive-admission and measurement provider.
<!-- SECTION:NOTES:END -->
