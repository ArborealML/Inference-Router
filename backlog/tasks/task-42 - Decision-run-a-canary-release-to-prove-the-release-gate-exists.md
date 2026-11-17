---
id: TASK-42
title: 'Decision: prove the release gate on a disposable tag before v0.1.0?'
status: Waiting
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T09:53:00Z'
updated_date: '2026-11-17T11:06:00Z'
labels:
  - release
  - ci
dependencies:
  - TASK-44
  - TASK-45
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

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T10:58:00Z

---

Reassigned under decision-16: an agent runs it. Wake: [TASK-44](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-44%20-%20Close-what-the-eighth-release-gate-review-found-round-8-over-v0.12.15.md) merged and [TASK-45](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-45%20-%20Enable-GitHub-immutable-releases-before-the-first-tag.md) done, with CI green on the merge commit. Protocol, tightened by the round-8 review: tag current `main` as `v0.0.0-canary`, push, let the unmodified workflow run with its own token, capture the create and upload responses, the full asset inventory, the download SHA-256, the peeled tag SHA, the `PATCH` response, and `.immutable` on the published release; then try to replace an asset and to move the tag and **require both to be refused**; then delete the release with `--cleanup-tag`, because preflight refuses a tag that already carries a release, draft or published. Cost: public assets for a few minutes plus one runner leg. It is a mutation of a public repository and the report says so in those words.

---

created: 2026-11-17T11:06:00Z

---

Hardening from the TASK-39 answer: the canary is also the test bed for consumer verification. On the published canary, run the readback against the live URLs and try the documented user verification path; record the gh version used. Measured here: gh 2.46.0 has none of `gh release verify`, `gh release verify-asset`, `gh attestation` (each exits 1), so if the release README is to tell users those commands, the canary is where the version floor gets established — and if it tells them `sha256sum` plus a REST listing, the canary proves that works too.

---
<!-- COMMENTS:END -->
