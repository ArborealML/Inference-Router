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
Found by an external review of the priority branch and verified in the code: the admin routes catch only JsonException, so InvalidOperationException from TryGetProperty/GetString escapes. This is pre-existing and not caused by the priority change, but it is on the same surfaces the priority work touched.

Candidate ee2fbc8ee2ff7b321a0d2a6d4733cb1f5aaea93f implements AC1–3 under the clarified existing target-reset exception. Independent plan and source review passed; source range 8a12ba8..ee2fbc8 had no blocking findings. Exact-head Release verification: 92 passed, zero failed/skipped across AdminJsonBodyTests, AdminThreadPriorityTests, AdminSwitchOperationTests, ErrorContractTests and ConfigReloadWireTests; build/analyzers passed. Fixtures pin literal refusals, valid nulls and no registration/admin-use/operation effects.

Dependency base 2c54500 merges exact PR #86 head a12012b176e84e49603c433a9f934206499b4bcb with planning main 62f1c79. PR #86 priority code is unchanged and retains its separate review dispositions. SPEC v0.12.166 and README state the correction. Coordinator next publishes this stacked increment, checks hosted build/test/AOT on its actual head and reconciles after the prerequisite lands. No merge/live qualification; not Done. Backlog CLI unavailable; controlled Markdown and strict validation substitute.
<!-- SECTION:NOTES:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Check object root once and validate expected string kinds before RegisterThread, admin-use acquisition or operation reservation. Cover threads threadId/class/modelAffinity and switch engine/model/profile; preserve supported optional nulls and required-field wording. Reuse PR #86's priority object/class/sub validation. Keep target-reset invalid_target_reset and auth/ETag precedence unchanged; reload has no JSON parser.

Pin exact refusal envelopes for six nonobject roots and malformed string members, with no thread registration, admin use or operation reservation. Run AdminJsonBodyTests, existing priority parsing fixtures, ConfigReloadWireTests, ErrorContractTests and AdminSwitchOperationTests. Update SPEC/README and request bounded source review. No live backend operations or unrelated PR #86 corrections.
<!-- SECTION:PLAN:END -->
