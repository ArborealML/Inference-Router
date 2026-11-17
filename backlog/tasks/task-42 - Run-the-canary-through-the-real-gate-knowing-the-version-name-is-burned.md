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
9910c0f) answers `immutable: true`, prerelease, five assets. What this task still owns is the half only a *published*
release can give: probing it after publication — an asset replacement attempt and a tag move attempt, both required to be
refused, plus `.immutable` read back — and its disposition, kept as a permanent prerelease or deleted, the name reserved
either way.

**Cost per retry, measured.** `v0.0.9-canary` never got a release, so its name is unburned, but the `v*` ruleset refuses
to move or delete that tag: **every retry needs a new version name**, and the refused tags stay as immovable furniture.
That is the price of proving the gate in public, and it is why the negative cases belong in the disposable repository
(TASK-49), where names cost nothing.

**Not proven here, and stated as unproven in SPEC:** whether `POST /releases` rejects a second release on an existing tag
with a `422` — never observed, and rehearsing it here would spend another name.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 the decision to run is recorded and the run reported with the unmodified workflow and the job's own token, against the canary name actually used
- [ ] #2 the run answers, as evidence: whether `contents: write` from `GITHUB_TOKEN` suffices for asset upload plus the un-draft `PATCH`, and whether the workflow-write guard fires for a tag cut at the head of `main`
- [x] #3 the run records what `.target_commitish` says on a real create, and what the tag object SHA and the peeled commit SHA were
- [ ] #4 the published canary is probed after publication: an asset replacement attempt and a tag move attempt, both required to be refused, plus `.immutable` read back
- [ ] #5 the canary's disposition is decided and recorded here: kept as a permanent prerelease, or deleted — the name stays reserved either way
- [ ] #6 nothing auto-deletes a release whose disposition is uncertain; if a probe is skipped, the skip and its reason are recorded here
- [ ] #7 the run leaves what TASK-48 needs to prove a download: a published release, its asset inventory, and the digests of the bytes actually served
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**Wake, as events:** TASK-49's disposable repository existing, and the gate work closed on TASK-44 with CI green on the
commit the canary points at. The ruleset and immutable releases are in place and were read back.

**Order, as performed.** The ruleset and immutable releases came first, because a canary against an unprotected repository
proves nothing about protection. The live ruleset protects `update` and `deletion` and leaves `creation` out, so pushing a
new `v*` tag succeeds — measured twice. Whether a `creation` rule with `bypass_actors: []` would have blocked the pusher
is untested, and that probe belongs in the disposable repository.

**Canary 1 is evidence the gate works, not that it failed.** It built and smoke-tested on both legs and then refused to
publish: the gate byte-compared a checksum record whole, and Windows writes that record in binary mode where GNU coreutils
writes text — equal digests, unequal lines, and the message blamed the digest. Fixed at 9910c0f (v0.12.23), harness to 132
cases; the round's row is in [SPEC.md](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md) §15.

**Proves only what the tagged commit's workflow can do** — the floor rule is a criterion on TASK-28, and the gh version
floor (no `gh release verify` or `gh attestation` in 2.46.0) is on TASK-48.

Superseded log entries: [board trim, 2026-11-17](https://github.com/ArborealML/Inference-Router/blob/main/backlog/archive/board-trim-2026-11-17.md).
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
<!-- COMMENTS:END -->
