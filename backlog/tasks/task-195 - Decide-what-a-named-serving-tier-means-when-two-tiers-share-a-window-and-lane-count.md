---
id: TASK-195
title: Bind exact recipe identity when context and lane counts coincide
status: Ready for Review
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-26T07:34:00Z'
updated_date: '2026-11-28T11:31:00Z'
labels:
  - config
  - scheduling
milestone: m-18
dependencies:
  - TASK-189
references:
  - src/Arboreal.InferenceRouter/Scheduling/InProcessHostManager.cs
modified_files:
  - README.md
  - SPEC.md
  - src/Arboreal.InferenceRouter/Config/DockerTargetPlans.cs
  - src/Arboreal.InferenceRouter/Engines/EngineRuntime.HostLifecycle.cs
  - src/Arboreal.InferenceRouter/Engines/EngineRuntime.cs
  - src/Arboreal.InferenceRouter/Engines/Lifecycle/HostClosedRouteStaging.cs
  - src/Arboreal.InferenceRouter/Engines/Lifecycle/HostLifecycleCoordinator.cs
  - src/Arboreal.InferenceRouter/RouterCore.Admission.cs
  - src/Arboreal.InferenceRouter/RouterCore.cs
  - src/Arboreal.InferenceRouter/Scheduling/CapabilityScan.cs
  - src/Arboreal.InferenceRouter/Scheduling/ExactTicketAdmission.cs
  - src/Arboreal.InferenceRouter/Scheduling/InProcessHostManager.cs
  - src/Arboreal.InferenceRouter/Scheduling/LocalLifecycleGroup.cs
  - src/Arboreal.InferenceRouter/Scheduling/PrefixAwareScheduler.cs
  - src/Arboreal.InferenceRouter/Scheduling/VerifiedDispatchPreparation.cs
  - >-
    tests/Arboreal.InferenceRouter.Tests/HostClosedRouteStagingTests.DispatchRecovery.cs
  - >-
    tests/Arboreal.InferenceRouter.Tests/HostClosedRouteStagingTests.RouterComposition.cs
  - tests/Arboreal.InferenceRouter.Tests/ManagedVllmProfileTests.cs
priority: medium
ordinal: 187000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Equal window/lane recipes may differ in weights, quantization, KV or resource settings. Own stable exact recipe identity and ambiguity refusal, not a second implementation of serving-instance verification. Numeric suitability may use a qualified resident independently of its display label.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Distinct effective settings have distinct stable identities; equivalent resolved aliases/defaults share identity.
- [ ] #2 Ambiguous exact launch selection refuses before mutation; selected recipe identity is frozen through the operation.
- [ ] #3 Numeric context-only selection can reuse a verified fitting resident without interpreting ambiguous display labels as proof.
- [ ] #4 SPEC §§5 and 7.6 distinguish suitability, display inference and exact identity; fixture tests cover equal numeric pairs.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Resolve model and tier aliases against the captured catalog before validation, canonicalizing separately for each engine. Resolve exact named or cold default intent once. Define a deterministic documented cold choice or refuse unresolved ambiguity before recovery, peer stops or route withdrawal. Keep unnamed warm requests on a qualified fitting resident, independently of its display label or launch default. Preserve legacy named numeric semantics where promised. Never downgrade unsupported exact intent to a numeric request.

Reuse the compiled target projection and existing plan digest. Carry one immutable selection and semantic mode from ranking through the domain claim, preparation, exact take and submission. Rank exact mismatches for supported activation or definitive refusal. Confirm qualified resident and config, operation, route and instance generations at take and through the existing first-write gate for streamed and ordinary responses. Preserve existing checkpoint serialization and the native adoption verifier.

Test the real equal-capacity Strata pair with differing cache settings in projection, and a supported enrolled fixture for ordinary admission. Cover exact mismatch and same-recipe no-op, alias and default equality, unnamed fit, unsupported cold selection with zero side effects and unchanged-gauge replacement at preparation, take and first write. Update SPEC and README with the qualified scope.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Review against 3c7ae3e confirmed the named-tier pre-enqueue lookup, conflicting numeric rank versus exact admission, resident-derived sender binding, and the need to reject ambiguity before mutation. The plan now names each gate, the separate compatibility modes, and end-to-end controls. [The candidate](https://github.com/ArborealML/Inference-Router/pull/97) passed 223 focused tests at a1c2498. The base failed all four streaming and non-streaming controls: two alias requests were refused and two canonical requests used the wrong equally sized recipe. The cold numeric path skips unsupported equal-window choices. Source review and hosted CI are active. Existing instance and launch-capacity work retains production qualification.
<!-- SECTION:NOTES:END -->
