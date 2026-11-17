---
id: TASK-42
title: 'Decision: run a canary release to prove the release gate exists'
status: Blocked
assignee:
  - '@claire'
created_date: '2026-11-17T09:53:00Z'
updated_date: '2026-11-17T09:53:00Z'
labels:
  - release
  - ci
dependencies: []
priority: high
ordinal: 41000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Do this (4 minutes plus runner time).** Answer one line here: **run the canary** or **no canary: reason**. If you
run it yourself, two commands — tag the current main HEAD, push it, watch the run, then delete:

    git tag v0.0.0-canary "$(git rev-parse origin/main)" && git push origin v0.0.0-canary
    gh release delete v0.0.0-canary --cleanup-tag --yes

Or answer `run the canary` and the coding session performs it and reports what the run proved.

**What it proves.** The release workflow has never run for real: no tag has ever been cut, so the gate that has been
hardened six times is configured, not proven. Five unknowns cannot be settled by any local test, only by a run against
the real API with the real token — what error the workflow-write guard actually returns (404 or 403, not the 422
earlier rounds assumed), whether the upload really reaches the uploads host with the token intact, what title and body
GitHub generates for the release, whether the asset digests match on real uploads, and whether the tag can move between
the preflight check and the create. One runner leg answers all of them.

**What it costs, stated plainly.** For a few minutes a public release with five downloadable assets exists on the
repository, under a version number nobody will mistake for a real one. Deleting the release and the tag afterwards is
part of the answer, and the preflight step refuses a tag that already carries a release — published *or* draft — so the
cleanup is not optional. It is a mutation of a public repository, which is why it is your call and not an agent's.

**What it does not prove:** that the commit the canary tags is the release you want to ship. [TASK-32](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-32%20-%20Decision-cut-the-v0.1.0-tag.md)
stays a separate yes.

**Links:** the workflow that will run — [release.yml](https://github.com/ArborealML/Inference-Router/blob/main/.github/workflows/release.yml);
where the release appears — [releases](https://github.com/ArborealML/Inference-Router/releases); the tag-floor rule is in
[TASK-32](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-32%20-%20Decision-cut-the-v0.1.0-tag.md).

**Why this is Blocked:** no event wakes it. It waits on the owner's one-line answer on this task, and [TASK-32](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-32%20-%20Decision-cut-the-v0.1.0-tag.md) and the first-tag task both sit behind it, so a decline stops the release path rather than merely delaying it.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Answer in one line: run the canary tag v0.0.0-canary, or no canary with a reason
<!-- AC:END -->
