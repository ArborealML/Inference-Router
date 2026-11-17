---
id: TASK-48
title: >-
  Prove what a downloader receives: post-publish readback and free build
  attestation
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T11:06:00Z'
updated_date: '2026-11-17T11:06:00Z'
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
task, so do not write it twice. Test the consumer commands on the canary in [TASK-42](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-42%20-%20Decision-run-a-canary-release-to-prove-the-release-gate-exists.md).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 before any upload the run writes a run-local map of exact asset name to staged sha256 and size, and that map — not a checksum file fetched later and not GitHub's reported digest — is the oracle the rest of the step compares against
- [ ] #2 after publication the publish job fetches each expected asset by its own published HTTPS download URL, streams the bytes into a fresh SHA-256, and requires the name-specific staged sha256 and size to match; a digest that appears somewhere in the expected set under the wrong name fails, and so does a missing name, an extra name, a duplicate, a non-uploaded asset, an HTML or redirect or TLS error, or a size mismatch; retries are bounded and never replace the expected hash
- [ ] #3 the published SHA256SUMS and per-archive sidecars are checked against that map, and the existing stored-digest comparison is kept rather than replaced
- [ ] #4 free keyless build attestation is produced for each archive inside the existing build jobs with a pinned attestation action (actions/attest has a v4 tag, verified by `git ls-remote` today), and the permissions diff it needs — `id-token: write` and `attestations: write` — is named in the commit message because it widens what the job can do
- [ ] #5 consumer verification is tested on the canary release rather than copied from docs: measured on this box, gh 2.46.0 has no `gh release verify`, no `gh release verify-asset` and no `gh attestation` (each exits 1 as an unknown command), so whatever the README tells a user to run must name the minimum gh version, or ship a REST-and-sha256 check that works today
- [ ] #6 a post-publish readback failure is treated as an incident with a written response — alert, remove or reissue, never repair published immutable assets in place — and SPEC's accepted-gaps table records what the readback cannot prove: it checks bytes served to one runner at one moment, and it happens after public exposure
<!-- AC:END -->
