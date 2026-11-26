---
id: TASK-202
title: Refuse admin JSON roots that are not objects instead of answering 500
status: To Do
assignee:
  - '@gpt-6.2-sol-config'
created_date: '2026-11-26T22:30:00Z'
labels:
  - wire
dependencies: []
priority: high
ordinal: 194000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Admin route bodies are read with JsonDocument and then treated as objects: a root array, a root number or a root string makes TryGetProperty or GetString throw an InvalidOperationException that no catch arm handles, so a caller who sent a refusible body gets a 500 with no error envelope. The fix is to check the root's kind once at each admin route and answer the existing 400 invalid_request, not to add a new code.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Every admin route that parses a JSON body refuses a root that is not an object with 400 invalid_request in the existing envelope.
- [ ] #2 A well-shaped root with a non-string member is refused, not thrown through.
- [ ] #3 The route's own refusal body is byte-pinned for each malformed root class, and no exception reaches the server's 500 path.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Found by an external review of the priority branch and verified in the code: the admin routes catch only JsonException, so InvalidOperationException from TryGetProperty/GetString escapes. This is pre-existing and not caused by the priority change, but it is on the same surfaces the priority work touched.
<!-- SECTION:NOTES:END -->
