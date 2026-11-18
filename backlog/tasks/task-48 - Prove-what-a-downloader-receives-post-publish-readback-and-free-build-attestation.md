---
id: TASK-48
title: >-
  Prove what a downloader receives: post-publish readback and free build
  attestation
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T11:06:00Z'
updated_date: '2026-11-18T08:32:00Z'
labels:
  - ci
  - release
  - security
dependencies:
  - TASK-44
  - TASK-45
priority: medium
type: task
ordinal: 47000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Adopted posture from [TASK-39](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-39%20-%20Decision-verify-what-a-downloader-receives-or-keep-accepting-that-we-do-not.md) (the external answer landed 11:12Z; adopted because it contradicts
nothing measured here): keep the stored-digest gate, turn on immutable releases ([TASK-45](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-45%20-%20Enable-GitHub-immutable-releases-before-the-first-tag.md)), read the served
bytes back inside the same job, and attest the build with free keyless OIDC. **No paid signing, no OpenPGP** —
that needs key custody, rotation and a distribution story for fingerprints, and none exists.

**Cost, checked rather than assumed: no new runner leg.** The readback is bounded network IO in the publish job —
it downloads what the job just uploaded. The attestation step runs in the build jobs that already exist.

**Why the readback is worth anything:** the stored-digest match is GitHub's metadata about bytes it says it kept.
Reading the public download URL back is a separate observation, over the route a user takes, and it catches the
boring failures — a wrong release or tag or filename route, a redirect, a truncated or stale serve, an HTTP error.
It does not identify who uploaded, and it cannot say what a client in another region at another time receives.
**The gap it cannot close: a compromised writer can publish a malicious archive with matching checksums, and
immutability then freezes the malicious set.** That is why the checksums stay documented as integrity references
and not as publisher authentication.

[TASK-44](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-44%20-%20Close-the-open-release-gate-review-findings-SPEC-v0-12-15-onward.md) lands first — same file, and its AC #5 (read `.immutable` after publication) is shared with this
task, so do not write it twice. Test the consumer commands on the canary in [TASK-42](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-42%20-%20Run-the-canary-through-the-real-gate-knowing-the-version-name-is-burned.md).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Before any upload the run writes a map of asset name to staged sha256 and size; that map, not a checksum file or GitHub digest, is the oracle
- [ ] #2 Every readback compares against that map, never a `SHA256SUMS` file or a `gh --jq` digest
- [ ] #3 After publication the job fetches each expected asset by its download URL, hashes it, and requires the staged sha256 and size to match
- [ ] #4 A missing, extra, duplicate, wrong-name or un-uploaded asset, a redirect, HTML or TLS failure, or a size mismatch fails it; retries never replace the expected hash
- [ ] #5 the published SHA256SUMS and per-archive sidecars are checked against that map, keeping the existing stored-digest comparison
- [ ] #6 Free keyless build attestation is produced per archive in the existing build jobs with a pinned action (`actions/attest` v4, checked by `git ls-remote`)
- [ ] #7 The permissions it needs — `id-token: write`, `attestations: write` — are named in the commit message
- [ ] #8 Consumer verification is measured, not copied: gh 2.46.0 has no release verify, verify-asset or attestation command
- [ ] #9 The README names a minimum gh version, or ships a REST-and-sha256 check that works on the version here
- [ ] #10 A post-publish readback failure is an incident with a written response: alert, remove or reissue; never repair published assets in place
- [ ] #11 SPEC's accepted gaps say plainly that the readback proves bytes served to one runner at one moment, after publication
- [ ] #12 For a published tag, the tracked harness fetches each asset over HTTPS outside the build run and proves its bytes hash to the recorded digest.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Measured 08:22Z: neither canary run carries an attestation — the attestations endpoint answers 404 for both — so AC #6 starts from nothing.
<!-- SECTION:NOTES:END -->
