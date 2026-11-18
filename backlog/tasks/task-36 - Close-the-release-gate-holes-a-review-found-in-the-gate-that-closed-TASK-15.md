---
id: TASK-36
title: Close the release-gate holes a review found in the gate that closed TASK-15
status: Done
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T08:18:00Z'
updated_date: '2026-11-18T19:29:00Z'
labels:
  - ci
  - release
milestone: m-9
dependencies: []
priority: medium
ordinal: 35000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Disposition of the external deep reviews of 713ffdd and fbc48d6, recorded as its own task rather than bolted onto
TASK-15, because TASK-15's criteria (drop the osx legs, gate the two declared ones) were met and reopening it would
misreport the board.

**What the reviews found.** The gate could pass unchecked bytes. Its checksum step verified the filenames written
INSIDE each sidecar rather than the archive that carried it, so two sidecars naming the linux archive plus one
unrelated win-x64 archive satisfied every check and would have shipped. A flattened download directory let one
basename arrive twice. Nothing required an archive to be an archive. The publish step globbed, so a leg nobody
declared would have shipped ungated. And the cleanup step could delete a draft this run did not create.

**What landed** in [6aa26cc](https://github.com/ArborealML/Inference-Router/commit/6aa26cc): each archive is
compared against the whole record the leg that built it wrote (digest plus filename together), so a sidecar pointing
elsewhere fails; files no declared leg produces are refused; a repeated basename is refused; each tar must unpack
and contain its own `air`/`air.exe` entry; and the upload list is built from the same expected-RID list the gate
reads. After publishing, the run reads the release's actual asset inventory and draft/prerelease flags before
calling itself successful, and a half-finished draft is deleted by release id only when it was created at or after
the run started. SPEC carries it as v0.12.9 with the published-surface bullet in §14; the README now says the
checksums are a damage check, not provenance.

**Verified by the board on the checked-out file, not from the report:** matrix legs parse to linux-x64 and win-x64;
the expected-RID list is referenced by both the gate and the upload path; the basename, unpack and asset-inventory
guards are present (asset list at release.yml:295, flags at 301 and 308, leftover draft selected by id and
created_at at 324, DELETE by id at 334).

**What the board did not run:** the gate itself against forged artifacts. Those exercises are the coding session's
and the reviews'. And no tag has ever been published, so the gate has still never run end to end in CI — that is
TASK-28, and the README/SPEC now say the surface is configured rather than proven.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Each published archive is verified against the digest AND filename its own build leg recorded, not against names written inside a sidecar
- [x] #2 An artifact no declared leg produces, a repeated basename, and a file that is not the archive it claims to be are all refused
- [x] #3 The upload list is built from the same expected-RID list the gate reads, so an undeclared leg cannot ship
- [x] #4 Success is read from the published release asset inventory and flags, and a leftover draft is removed by id only when this run created it
<!-- AC:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T08:20:00Z

---

Opened and closed by the board on one pass, at the coding session's request, so a review finding has a carrier instead of living in a chat message. Board question answered: option (a), a task of its own, because TASK-15's three criteria are true and unchanged - work done in response to a review of Done work gets a next commit plus a carrier, not a reopened task. Box checks rest on the board reading the checked-out workflow, item by item, plus the SPEC v0.12.9 line; the forgery exercises themselves are credited to the coding session and are not claimed here.

---
<!-- COMMENTS:END -->
