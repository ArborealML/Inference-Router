---
id: TASK-202
title: Refuse admin JSON roots that are not objects instead of answering 500
status: Done
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-26T22:30:00Z'
updated_date: '2026-11-28'
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
- [x] #1 Every admin route parsing a JSON body refuses nonobject roots with its existing 400 envelope: invalid_request for threads/switch, invalid_target_reset for target-reset.
- [x] #2 A well-shaped root with a non-string member is refused, not thrown through.
- [x] #3 The route's own refusal body is byte-pinned for each malformed root class, and no exception reaches the server's 500 path.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Invalid administrative JSON returns the route-specific 400 response before registration or switch effects. [Reviewed implementation](https://github.com/ArborealML/Inference-Router/pull/90) is included in [main](https://github.com/ArborealML/Inference-Router/commit/1bf85d3495ab2517a921d78013ffe69bbefb4325). [Hosted qualification](https://github.com/ArborealML/Inference-Router/actions/runs/46112270273) passed 2,269 tests and the Linux native build. The [publication record](../milestones/wave1-implementation-evidence.md) preserves the original review and fixture history.
<!-- SECTION:NOTES:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Check object root once and validate expected string kinds before RegisterThread, admin-use acquisition or operation reservation. Cover threads threadId/class/modelAffinity and switch engine/model/profile; preserve supported optional nulls and required-field wording. Reuse PR #86's priority object/class/sub validation. Keep target-reset invalid_target_reset and auth/ETag precedence unchanged; reload has no JSON parser.

Pin exact refusal envelopes for six nonobject roots and malformed string members, with no thread registration, admin use or operation reservation. Run AdminJsonBodyTests, existing priority parsing fixtures, ConfigReloadWireTests, ErrorContractTests and AdminSwitchOperationTests. Update SPEC/README and request bounded source review. No live backend operations or unrelated PR #86 corrections.
<!-- SECTION:PLAN:END -->
