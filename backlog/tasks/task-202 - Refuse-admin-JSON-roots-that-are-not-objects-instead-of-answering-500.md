---
id: TASK-202
title: Refuse admin JSON roots that are not objects instead of answering 500
status: In Progress
assignee:
  - '@gpt-6.2-sol-shutdown'
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

Independent pre-code review passed the bounded plan below. AC1 clarifies the existing target-reset exception to preserve AC3's route-specific wire contract; no new refusal code or owner decision. Dependency branch includes exact PR #86 head a12012b176e84e49603c433a9f934206499b4bcb and current planning main 62f1c79. Preserve PR #86 priority parsing unchanged; final PR remains dependent on its integration and follow-up reconciliation. SPEC revision reserved: v0.12.166.
<!-- SECTION:NOTES:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Check object root once and validate expected string kinds before RegisterThread, admin-use acquisition or operation reservation. Cover threads threadId/class/modelAffinity and switch engine/model/profile; preserve supported optional nulls and required-field wording. Reuse PR #86's priority object/class/sub validation. Keep target-reset invalid_target_reset and auth/ETag precedence unchanged; reload has no JSON parser.

Pin exact refusal envelopes for six nonobject roots and malformed string members, with no thread registration, admin use or operation reservation. Run AdminJsonBodyTests, existing priority parsing fixtures, ConfigReloadWireTests, ErrorContractTests and AdminSwitchOperationTests. Update SPEC/README and request bounded source review. No live backend operations or unrelated PR #86 corrections.
<!-- SECTION:PLAN:END -->
