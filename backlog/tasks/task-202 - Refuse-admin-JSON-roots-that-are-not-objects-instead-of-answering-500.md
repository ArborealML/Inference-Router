---
id: TASK-202
title: Refuse admin JSON roots that are not objects instead of answering 500
status: Ready for Review
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-26T22:30:00Z'
updated_date: '2026-11-27'
labels:
  - wire
dependencies: []
priority: high
ordinal: 194000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Admin route bodies are read with JsonDocument and then treated as objects: nonobject roots or malformed string members can throw InvalidOperationException through the 500 path. Guard the shape before effects and preserve each route's existing refusal envelope. Threads/switch use invalid_request; target-reset already guards shape and retains invalid_target_reset.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Every admin route parsing a JSON body refuses nonobject roots with its existing 400 envelope: invalid_request for threads/switch, invalid_target_reset for target-reset.
- [ ] #2 A well-shaped root with a non-string member is refused, not thrown through.
- [ ] #3 The route's own refusal body is byte-pinned for each malformed root class, and no exception reaches the server's 500 path.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Published [PR #90](https://github.com/ArborealML/Inference-Router/pull/90), head ac9d783b8fcc84c4fd95278953f2fceda0af932f. Dependency branch stack/task202-pr86-main at b46824d5245166122c455591802acd92c1f896e6 preserves PR #86 a12012b and planning main 62f1c79; their merge is Backlog-only. [Mappings](../milestones/wave1-publication-map.json) retain reviewed 8a12ba8..ee2fbc8 under TASK-220's provenance exception.

The pre-existing JsonElement kind exception was independently verified. AC1–3 implemented with target-reset's existing error-code exception. Plan/source review passed without blocking findings. Local Release at ee2fbc8: 92 passed, zero failures/skips; build/analyzers passed. Fixtures pin literal refusals, valid nulls and absent registration/admin-use/reservation effects. SPEC v0.12.166 and README state scope.

PR #86 priority code and its outstanding review/client dispositions remain unchanged. [Hosted CI](https://github.com/ArborealML/Inference-Router/actions/runs/46112270273) passed on this head: 2,269 tests, zero failed/skipped, build, Linux AOT/config smoke, strict board and release harness. Next: reconcile after #86 lands before final integration approval. No live qualification or Done claim. Backlog CLI unavailable; strict controlled Markdown substitutes.
<!-- SECTION:NOTES:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Check object root once and validate expected string kinds before RegisterThread, admin-use acquisition or operation reservation. Cover threads threadId/class/modelAffinity and switch engine/model/profile; preserve supported optional nulls and required-field wording. Reuse PR #86's priority object/class/sub validation. Keep target-reset invalid_target_reset and auth/ETag precedence unchanged; reload has no JSON parser.

Pin exact refusal envelopes for six nonobject roots and malformed string members, with no thread registration, admin use or operation reservation. Run AdminJsonBodyTests, existing priority parsing fixtures, ConfigReloadWireTests, ErrorContractTests and AdminSwitchOperationTests. Update SPEC/README and request bounded source review. No live backend operations or unrelated PR #86 corrections.
<!-- SECTION:PLAN:END -->
