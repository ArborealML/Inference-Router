---
id: TASK-43
title: Close the four release-gate defects the fifth review found (SPEC v0.12.14)
status: Done
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T09:53:00Z'
updated_date: '2026-11-17T10:58:00Z'
labels:
  - ci
  - release
  - security
dependencies: []
priority: high
ordinal: 42000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Round 7 carrier. Round 6 reviewed e4b4683 and confirmed the round-5 repair is right - the response-captured id is the documented identity of the draft, and gh 2.46.0 --input sends the file as a raw reader with no JSON re-encoding - while finding four defects the coding session is fixing in v0.12.14: the second inspection re-reads the draft flags but not the draft tag_name (the git ref is proven, the release object is not); the release title is uncontrolled because generate_release_notes with no name lets GitHub invent one; upload_url is only checked for non-empty rather than for the exact uploads.github.com shape for this repository and id; and the workflow-write guard's real behaviour is 404, sometimes 403, with documented ambiguity about whether it fires when the tag already exists. Wake: the deep review of the v0.12.14 commit. Do not cut a tag while this round is open.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 The draft's tag_name is re-checked against the expected tag in the final inspection, not only its flags
- [x] #2 The release title is pinned by passing the tag as the release name on create
- [x] #3 upload_url is validated against the exact expected shape for this repository and release id
- [x] #4 The workflow-write guard is described by its observed status codes, not by the 422 earlier rounds assumed, and the ambiguity about an existing tag is written down as ambiguity
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Confirmed against the revision log: bff39e8 carries v0.12.14, the fifth deep review's fixes (SPEC line 355). The review now in flight was dispatched 10:04Z over that commit; its findings would land as v0.12.15 and need their own carrier, not this one.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T10:18:00Z

---

Four boxes checked by reading the checked-out workflow: the release object's tag_name is compared in the pre-PATCH inspection (release.yml:438) and again in the post-publish read-back (462); the title is pinned with name=$TAG on create (361); upload_url is compared against the exact expected shape for this repository and id (380, used at 396); and the create response is validated as a whole - id, upload_url, tag_name, draft, prerelease (363). AC #4, describing the guard's real status codes, is closed by the wording now on TASK-41 and TASK-28: 404, sometimes 403, never 422, with the existing-tag case documented as ambiguity. Wake remains the review of this commit, which is the round-7 review; a declined or clean review closes the task.

---

created: 2026-11-17T10:58:00Z

---

Closed by the round-8 review, which landed 2026-11-17 10:58Z and is the wake this task was
parked on. It confirms all four criteria: the exact-template compare "is right, stronger than prefix matching";
adding id and name to the read-backs is "not decoration"; excluding `.target_commitish` for an existing tag is
"right". It also opened five new items — over-broad case folding, the URI-template suffix, the absent-tag
fallback, the discarded `PATCH` response, and whether `contents: read` can create a release at all — which
carry on in [TASK-44](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-44%20-%20Close-what-the-seventh-release-gate-review-found-over-v0-12-15.md). Harness re-run on this tree at close: 59 cases, 0 failures.

---
<!-- COMMENTS:END -->
