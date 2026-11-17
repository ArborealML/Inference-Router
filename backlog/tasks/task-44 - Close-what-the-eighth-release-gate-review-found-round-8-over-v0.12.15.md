---
id: TASK-44
title: Close what the eighth release-gate review found (round 8 over v0.12.15)
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T10:52:00Z'
updated_date: '2026-11-17T10:58:00Z'
labels:
  - ci
  - release
  - security
dependencies: []
priority: high
type: task
ordinal: 43000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Round-8 carrier: do not cut a tag while it is open.** The eighth deep review ran over
c6c73f7 (v0.12.15). It confirms the round-7 repairs — the exact-template predicate is the right predicate, the
id-and-title read-back is "not decoration", excluding `.target_commitish` for an existing tag is "right" — and
leaves five findings. They are evidence, not instruction: check each against this workflow and the harness
before changing anything.

1. Case folding is too broad: only host and owner/repo are case-insensitive. `/repos/`, `/releases/`,
   `/assets`, the release id and the literal `{?name,label}` suffix are not.
2. `{?name,label}` is a URI-template expression, not a query. Require the literal suffix in the response, strip
   it before uploading, encode `name` separately, refuse an already-expanded query.
3. Absent-tag fallback: if the tag disappears between the first resolution and the create, GitHub creates it
   from the default branch. Pass `target_commitish=<pinned sha>` and keep both the tag object SHA and the
   peeled commit SHA in the checks.
4. The publish `PATCH` response is thrown away. Reading it costs no extra request; and a failure after the
   PATCH means something may already be public, which the message has to say.
5. **The one that can stop the first tag cold:** the job runs at `contents: read`. Establish by evidence
   whether a `GITHUB_TOKEN` at that permission can create a release, upload its assets and un-draft it. If it
   cannot, the first tag fails at create.

Harness: `.local/relgate.py`, **59 cases, 0 failures**, re-run on this tree and counted. Unchanged limits: a
scripted `gh` over a stub HTTP layer proves control flow, not GitHub's authorization, the bytes on the wire, or
first-tag success. Cost: an afternoon plus a SPEC revision.

[TASK-45](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-45%20-%20Enable-GitHub-immutable-releases-before-the-first-tag.md) is the setting this review asked for; [TASK-43](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-43%20-%20Close-the-four-release-gate-defects-the-sixth-review-found-v0.12.14.md) is the previous round's carrier and is closed.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 upload_url is compared case-insensitively only where GitHub says the name is case-insensitive (host, owner/repo); the path literals, the release id and the literal {?name,label} template suffix are compared as written, and an already-expanded or unexpected query is refused
- [ ] #2 the create POST passes target_commitish=<the pinned commit SHA> so the absent-tag fallback cannot create the tag on the default branch, and the gate keeps both the tag object SHA and the peeled commit SHA in its checks
- [ ] #3 the publish PATCH response is read and validated instead of discarded, and the post-publish read-back still stands as the record of what went public
- [ ] #4 the release job's effective permissions are established by evidence, not assumption: whether GITHUB_TOKEN at contents: read can create a release, upload its assets and un-draft it, and if it cannot, the workflow says what it grants and why
- [ ] #5 when immutable releases are enabled the gate reads .immutable on the published release and records it, and SPEC states what immutability does not lock: drafts, the pre-publish window, the title and notes body, and the prerelease/latest flags
- [ ] #6 SPEC carries a round-8 disposition row naming each finding's disposition, and the revision log line is in the same commit as the code
<!-- AC:END -->
