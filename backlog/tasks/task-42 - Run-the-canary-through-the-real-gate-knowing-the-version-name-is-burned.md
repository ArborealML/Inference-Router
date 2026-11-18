---
id: TASK-42
title: Run the canary through the real gate, knowing the version name is burned
status: Waiting
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T09:53:00Z'
updated_date: '2026-11-18T20:57:00Z'
labels:
  - release
  - ci
milestone: m-9
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
- [x] #4 Record the measured asset-replacement and tag-update outcomes and the controls that protect delivered bytes and tag refs. The disposable laboratory is an accepted venue.
- [x] #5 the canary's disposition is decided and recorded here: **kept** as a permanent prerelease, the owner's word at 18:5xZ (`keep`) — the name stays reserved either way
- [ ] #6 nothing auto-deletes a release whose disposition is uncertain; if a probe is skipped, the skip and its reason are recorded here
- [ ] #7 TASK-48 has what it needs to prove a download: a published release, its asset inventory, and digests of the bytes served
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**Wake, as events:** TASK-49's disposable repository existing, and TASK-44 closed with CI green on the commit the canary
points at. Ruleset and immutability are in place and were read back.

**Order, as performed, and canary 1's near-miss: superseded narrative, kept in
[board trim, 2026-11-17](https://github.com/ArborealML/Inference-Router/blob/main/backlog/archive/board-trim-2026-11-17.md).**
Ruleset and immutability came first; canary 1 built both legs and was refused at publish by the gate's own checksum
comparison, fixed at 9910c0f (v0.12.23), 132 harness cases.

**Immutability protects less than this task assumed — measured in the laboratory at 19:2xZ.** On a published immutable
release an asset was deleted (`204`) and a different file uploaded under the same name (`201`), and the download URL serves
the new bytes; `PATCH` answers 200; with no ruleset `git push -f` moved that release's tag. The flag holds only the version
name. So the ruleset is the tag ref's one measured protection, and asset integrity rests on the gate's digest records and
the post-publish readback — TASK-48, now load-bearing.

Superseded log entries: [board trim, 2026-11-17](https://github.com/ArborealML/Inference-Router/blob/main/backlog/archive/board-trim-2026-11-17.md).

At the tagged 9910c0f, the diagnostic fixes in 806d10d were already ancestors. [Original attempt](https://github.com/ArborealML/Inference-Router/actions/runs/44145829591/attempts/1) passed preflight, Linux and Windows smoke, and publish. The second attempt failed preflight and skipped build and publish. TASK-44 now tracks a fixture weakness, independent of the observed publication.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
<!-- COMMENTS:END -->
