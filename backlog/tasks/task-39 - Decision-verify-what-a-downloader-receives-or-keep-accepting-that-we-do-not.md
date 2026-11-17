---
id: TASK-39
title: 'Decision: verify what a downloader receives, or keep accepting that we do not'
status: Waiting
assignee:
  - '@chatswarm'
created_date: '2026-11-17T09:11:00Z'
updated_date: '2026-11-17T11:02:00Z'
labels:
  - release
  - ci
dependencies: []
priority: medium
ordinal: 38000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->

**Do this (2 minutes).** Answer one line here: `keep the gap, revisit when people install rather than build`, or `add the download-and-hash leg`, or `do signing`. One of the three costs runner minutes (the second) or key custody (the third); the first costs nothing and is already written down as an accepted gap in the spec.

**What is being decided, in one sentence:** whether the release pipeline should prove what a downloader actually
receives, or keep proving only what GitHub says it stored — the second is where it stands, and it is written down as
an accepted gap rather than an oversight.

**Answer format (one line):** `keep the gap, revisit at TRIGGER`, or `add the download-and-hash leg`, or `do signing`.
Cost: about 2 minutes to answer; option 2 is roughly an hour of implementation plus per-release runner minutes.

**Links** — the disposition row that records the gap: [SPEC §15](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md#L1445);
the published-surface acceptance criteria: [SPEC §14](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md#L1346);
the gate itself: [release.yml](https://github.com/ArborealML/Inference-Router/blob/main/.github/workflows/release.yml);
the task that runs the first release: [TASK-28] (https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-28%20-%20Cut-the-first-release-tag-and-watch-the-release-run.md).

<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 One line: keep the accepted gap with a named trigger, add a post-publish download-and-hash leg (runner minutes), or add signing or attestation (trust anchor)
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->

Reasoning and evidence moved out of the description under the answer-first rule (decision-15):

**Where the line currently sits.** The gate builds each binary on its own platform, runs it there, uploads the archive
plus a per-archive checksum, and then — after upload — reads back the published asset's name, upload state, byte size
and the digest GitHub reports for the bytes it stored, and refuses to publish if any of that does not match the file
it staged. That is real: it catches a wrong or truncated artifact, a mismatched upload, and a build that did not come
from the tagged commit. What it does not do is download the published asset and hash it, so it proves *GitHub's
account of storage*, not the bytes at the end of the download path. The published checksums and SHA256SUMS exist for
exactly that reason: they are the downloader's check, not the publisher's proof. There is no signing and no
attestation.

**Options and what each costs:**
1. **Leave it as the accepted gap** (what the spec says today). Cost: nothing. A downloader who does not verify the
   published checksums gets whatever the CDN gives them; a downloader who does is covered.
2. **Add a post-publish verification leg**: after un-drafting, a fresh job downloads each asset over the public URL
   and compares it against the checksums, failing loudly on mismatch. Cost: one more CI job — runner minutes you have
   told me you do not want to spend on macOS, and this is the same coin, roughly one job's worth per release — plus
   the honesty caveat that a job running in the same trusted environment is closer to the publisher than to the
   downloader, so it catches corruption, not deception.
3. **Signing or attestation** (cosign-style signatures, or GitHub artifact attestation): makes verification meaningful
   against a hostile publisher, not just a broken one. Cost: a trust anchor you have to keep, key custody, and a
   documented verification step that users actually have to follow — the part that usually rots.

**Recommendation:** option 1 for v0.1.0, with the trigger written down — reopen when binaries are being installed by
people who did not build them, which is the moment verification stops being optional. Option 2 is worth its runner
minutes only if releases become frequent enough that silent corruption would matter more than the once-per-release
manual check the first tag run already includes.

Round 5 puts a concrete sub-option on the table: byte fidelity of the upload leg is currently proved only by GitHub's own reported stored-bytes digest matching the staged file's sha256. That is enough to catch a mangled or truncated upload, and it is not an independent hash of the stored object. If you choose option 2 (a download-and-hash leg) or option 3 (signing), you are also closing that gap; if you choose option 1, the residual trust in the upload path is now named on TASK-41 as an accepted gap rather than left implicit.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T10:58:00Z

---

Reassigned under decision-16: this needs option analysis, not an owner's guess, and the recommendation is adopted unless it contradicts SPEC. Two facts have changed since it was written and belong in the brief: GitHub immutable releases are available and **off** on this repository (`GET .../immutable-releases` -> enabled:false), and will be turned on by TASK-45; and decision-10 already refuses paid macOS runner minutes, so the download-and-hash leg and any signing story have to argue their cost rather than assume it. Ask: given immutable releases plus the published SHA256SUMS sidecars, is a post-publish download-and-hash leg still worth runner minutes, or does the accepted gap stay with a named trigger?

---

created: 2026-11-17T11:02:00Z

---

Dispatched to @chatswarm 2026-11-17 11:02Z (tier high, reasoning-only) with the limits stated honestly rather than flattered: the stored-digest match proves GitHub-reported digest equals staged bytes at read time, not what a download serves; the gate never re-hashes the served bytes, and SPEC records that as an accepted gap. Two facts it is told: immutable releases are available on this repository and currently off, and will be enabled by TASK-45; and decision-10 refuses paid runner minutes, so anything added has to live inside the existing jobs — which is why a same-job download-and-hash step is judged on seconds, not on a new paid leg.

Wake: that answer landing, after which TASK-39 becomes either a written disposition or an implementation task for the coding session. Neither answer changes SPEC by itself.

---
<!-- COMMENTS:END -->
