---
id: TASK-42
title: Run the canary through the real gate, knowing the version name is burned
status: Waiting
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T09:53:00Z'
updated_date: '2026-11-17T17:39:00Z'
labels:
  - release
  - ci
dependencies:
  - TASK-38
  - TASK-44
  - TASK-45
  - TASK-49
priority: high
ordinal: 41000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The release gate has never run against a real tag. This run is the rehearsal that proves the gate exists — and
its cost is asymmetric: with immutable releases on, the version **name is burned either way**, so the canary is kept as a
permanent prerelease or deleted with the name still reserved. That is the shape of the decision; the
yes/no and the repository it runs in are answered on [TASK-49](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-49%20-%20Answer-the-throwaway-repository-question-for-the-destructive-gate-tests-spend-nothing-new.md),
and the gate work it exercises is [TASK-44](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-44%20-%20Close-the-open-release-gate-review-findings-SPEC-v0-12-15-onward.md).

**What this run must show, as facts not argument** (criteria below): whether `GITHUB_TOKEN` at `contents: write` is enough for asset upload plus the un-draft `PATCH`; what `.target_commitish` reports, and the tag-object versus peeled
commit SHAs; and what a published immutable release refuses when an asset is replaced or a tag moved.
**Not proven here**, and stated as unproven in SPEC: whether `POST /releases` rejects a second release on an existing
tag with a `422` — never tested. A rehearsal cannot rehearse that, so it stays open on TASK-44.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 the decision to run it is recorded (yes, under decision-16); the canary tag `v0.0.0-canary` is cut and the run reported with the unmodified workflow and the job's own token
- [ ] #2 The run answers, as evidence: whether `contents: write` from `GITHUB_TOKEN` suffices for asset upload plus the un-draft `PATCH`, and whether the workflow-write guard fires for a tag cut at the head of `main`
- [ ] #3 the run records what `.target_commitish` says on a real create, and what the tag object SHA and the peeled commit SHA were
- [ ] #4 the published canary is probed after publication: an asset replacement attempt and a tag move attempt, both required to be refused, plus `.immutable` read back
- [ ] #5 The canary's disposition is decided before the tag is cut and recorded here: kept as a permanent prerelease, or deleted — the version name stays reserved either way once immutable releases are on
- [ ] #6 Nothing auto-deletes a release whose disposition is uncertain; if the rehearsal is skipped, the skip and its reason are recorded here
- [ ] #7 the run leaves what [TASK-48](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-48%20-%20Prove-what-a-downloader-receives-post-publish-readback-and-free-build-attestation.md) needs to prove a download: a published release, its asset inventory, and the digests of the bytes actually served
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**Wake, as events:** TASK-49's repository existing, the ruleset read back on TASK-38, immutable releases enabled on TASK-45, the gate work closed on TASK-44, and CI green on the commit the canary tag points at.

**Order, and it is a dependency now.** A canary against an unprotected repository proves nothing about protection: the
ruleset on [TASK-38](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-38%20-%20Act-put-a-v-ruleset-on-the-repository-and-read-it-back.md) and immutable releases on [TASK-45](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-45%20-%20Enable-GitHub-immutable-releases-before-the-first-tag.md) come first. And a `creation` rule with
`bypass_actors: []` would block whoever pushes the canary tag, so TASK-38's actor list is a precondition of this run.

**No clean undo.** Deleting the canary's release frees the tag but the name stays reserved permanently, and turning
immutability off does not unlock it. Never auto-delete a release whose disposition is uncertain: cleanup's
`GET`-then-`DELETE` window plus a burned name is worse than a stray draft. If an extra runner leg is refused, record the
refusal and run it here; if allowed, rehearsing in a throwaway repository saves the name and the audience, not the
minutes.

**Proves only what the tagged commit's workflow can do.** The floor rule is a criterion on [TASK-28](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-28%20-%20Cut-the-first-release-tag-and-watch-the-release-run.md) and the gh
version floor (no `gh release verify` or `gh attestation` in 2.46.0) on [TASK-48](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-48%20-%20Prove-what-a-downloader-receives-post-publish-readback-and-free-build-attestation.md) — cited here, not repeated.
Superseded log entries: [board trim, 2026-11-17](https://github.com/ArborealML/Inference-Router/blob/main/backlog/archive/board-trim-2026-11-17.md).
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
<!-- COMMENTS:END -->
