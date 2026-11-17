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
**The gate has now run against a real tag, and the canary is published.** `v0.0.10-canary` (gate v0.12.23, tag peeled to
9910c0f) answers `immutable: true`, prerelease, five assets. What this task still owns is what only a *published* release
gives: the post-publication probes, and the disposition — kept as a permanent prerelease or deleted, name reserved either
way.

**Cost per retry, measured.** `v0.0.9-canary` got no release, so its name is unburned — but the `v*` ruleset refuses to
move or delete that tag, so **every retry needs a new version name** and refused tags stay as immovable furniture. That is
the price of proving the gate in public, and why the negative cases belong in the disposable repository (TASK-49).

**Not proven here, and unproven in SPEC:** whether `POST /releases` refuses a second release on one tag with a `422` —
never observed, and rehearsing it here would spend another name.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 the run is recorded and reported with the unmodified workflow and the job's own token, against the canary name used
- [ ] #2 evidence for whether `contents: write` from `GITHUB_TOKEN` suffices for upload plus the un-draft `PATCH`, and whether the workflow-write guard fires for a tag at the head of `main`
- [x] #3 the run records what `.target_commitish` says on a real create, and what the tag object SHA and the peeled commit SHA were
- [ ] #4 the refusal of an asset replacement and of a tag move on a **published immutable release** is observed, and the task records where it was measured — the disposable repository is an acceptable venue, the published canary is not
- [ ] #5 the canary's disposition is decided and recorded here: kept as a permanent prerelease, or deleted — the name stays reserved either way
- [ ] #6 nothing auto-deletes a release whose disposition is uncertain; if a probe is skipped, the skip and its reason are recorded here
- [ ] #7 TASK-48 has what it needs to prove a download: a published release, its asset inventory, and digests of the bytes served
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**Wake, as events:** TASK-49's disposable repository existing, and TASK-44 closed with CI green on the commit the canary
points at. Ruleset and immutability are in place and were read back.

**Order, as performed.** Ruleset and immutability first: a canary against an unprotected repository proves nothing about
protection. The live ruleset protects `update` and `deletion` and omits `creation`, so a new `v*` tag pushes fine —
measured twice; whether a `creation` rule with `bypass_actors: []` would have blocked the pusher is untested.

**Canary 1 is evidence the gate works, not that it failed.** Both legs built and smoke-tested, then publish refused: the
gate byte-compared a checksum record whole, and Windows writes it in binary mode where GNU coreutils writes text — equal
digests, unequal lines. Fixed at 9910c0f (v0.12.23), harness to 132 cases, row in
[SPEC.md](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md) §15.

**Proves only what the tagged commit's workflow can do**; the floor rule is on TASK-28 and the gh version floor (no `gh
release verify` or `gh attestation` in 2.46.0) on TASK-48.

**The tag-identity guard is already in the gate, measured in the open** (publish re-resolves the tag and refuses unless
its commit equals what the legs built, release.yml:500-510; checkout pins the same commit at release.yml:191). Owed still:
the **post**-publication peel re-check, and a SPEC line that `target_commitish` is not a pin — the canary answers `main`
there while the peeled tag is 9910c0f.

Superseded log entries: [board trim, 2026-11-17](https://github.com/ArborealML/Inference-Router/blob/main/backlog/archive/board-trim-2026-11-17.md).
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
<!-- COMMENTS:END -->
