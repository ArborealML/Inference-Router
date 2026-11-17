---
id: TASK-44
title: >-
  Close what the seventh and eighth release-gate reviews found (over v0.12.15
  and v0.12.16)
status: Waiting
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T10:52:00Z'
updated_date: '2026-11-17T12:02:00Z'
labels:
  - ci
  - release
  - security
dependencies: []
priority: high
type: task
ordinal: 43000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Wake: the ninth deep review of the gate, dispatched 11:55:18Z over 05f224b.** Carrier for the seventh and
eighth reviews (over v0.12.15 and v0.12.16). Ten of its eleven criteria are closed on the file; the eleventh waits
on immutability being switched on. **Do not cut a tag while this is open.**

**The two findings that changed what the shipped gate could do, stated plainly:** a `GET /releases/{id}` answering
`draft:false` and then `draft:true` passed cleanup's ownership guard and **deleted a published release**; and a
read-back answering somebody else's published release first let the run **go on to publish**, stopping only
afterwards. Both were reproduced against fd76021 itself as differential pairs, not argued.

**A third, found while re-reading rather than by the review:** the preflight release-existence check swallowed API
failures, so "could not read the release list" read as "no release for this tag" — and GitHub allows two releases
on one tag, so the previous revision would have built a second release on a published tag after a transient 5xx.
It fails closed now.

**What stays open is authorization, not parsing:** that `contents: write` from `GITHUB_TOKEN` suffices in practice
for upload plus un-draft, and whether the workflow-write guard fires for a tag cut at the head of main. Both are
criteria on the canary (TASK-42).

**Harness counts are as-of.** Measured by the board session at 05f224b: **90 cases, 0 failures**, re-run and
counted (73 at fd76021, 59 at c6c73f7 — each belongs to its own round). No criterion inside the release-job steps
is written as a line number; name the step or the exact command.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 upload_url is compared case-insensitively only over scheme, host and owner/repo, with the path from the release id onward compared byte-exact; the literal assets{?name,label} template suffix is required and cut at the brace; an already-expanded or unexpected query is refused (aa7ad23; case pub_url_path_case proves /Assets refuses)
- [x] #2 the target_commitish hardening is NOT taken. Original reasoning: the endpoint note makes the workflow-write guard a function of the RESOLVED target relative to the default branch, and the Actions GITHUB_TOKEN can never be authorized for it. Corrected in 05f224b: the claim that omitting it makes the comparison empty by construction is not guaranteed — the field is documented as unused when the tag already exists, so the resolved target is GitHub's, and both directions are canary evidence rather than a settled fact (SPEC 14)
- [x] #3 the un-draft PATCH response is read and checked as a release object, and its failure text does not imply nothing shipped (aa7ad23; case pub_patch_answers_draft shows rc!=0 while the release is public)
- [ ] #4 two things about authorization stay unproven and are stated as such: (i) that contents: write from GITHUB_TOKEN is sufficient in practice for asset upload plus un-draft — docs say yes, no run has shown it; (ii) whether the workflow-write guard fires for a tag cut at the head of main. Both are acceptance criteria on the canary in TASK-42, and our own tag commits touch release.yml
- [ ] #5 when immutable releases are enabled, the gate reads .immutable on the published release and records it, and SPEC states what immutability does not lock: drafts, the pre-publish window, the title and notes body, and the prerelease/latest flags (open behind TASK-45)
- [x] #6 SPEC carries the disposition row for each review naming every finding's disposition, with the revision-log line in the same commit as the code (v0.12.16 and v0.12.17 rows are on main)
- [x] #7 every check accepts exactly one answer: a multi-document response fails instead of being judged by its last document. This is what let v0.12.16 delete a PUBLISHED release on a lookup that answered draft:false then draft:true, and let a run go on to publish on a read-back that answered someone else's published release first (05f224b; reproduced as differential pairs against fd76021 itself: hole_cleanup_two_docs, hole_readback_two_docs)
- [x] #8 string hygiene anchors at the true end of input (\A..\z, not ^..$), so a trailing newline cannot clear an asset state or smuggle an asset name, and numeric ids are typed so fractional, negative and exponent values are refused before they are interpolated into an upload address (05f224b; pattern cases, with no demonstrated bypass claimed)
- [x] #9 the asset inventory refuses rather than paginates: a declared set of 100 or more is refused before create, because one full page is not an inventory (05f224b)
- [x] #10 the preflight release-existence check fails closed: an unreadable release list is an error, never 'no release for this tag'. Found while re-reading, not by the review. GitHub permits two releases on one tag, so the previous revision would have built a second release on a published tag after a transient 5xx (05f224b; the harness runs v0.12.16 failing open there)
- [x] #11 SPEC's own overstatements are corrected in place rather than kept: 'no server-chosen text reaches stdout' becomes the accurate claim that the success record is assembled from verified values; the asset state story is limited to what the documentation supports; and the target_commitish claim is corrected as in AC #2 (05f224b)
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Disposition of the seventh review's findings, by commit, so a later reader can
tell a decision from an oversight.

**Fixed in aa7ad23** (code + harness): response fields are compared as JSON with `jq -e` and typed equality, never
as columns of a `join(" ")` string re-split by `read` — a null collapses and a space inside a value becomes a
separator, so a create answer of `name "v0.1.0 true"` with `draft` absent used to pass four comparisons at once,
and a cleanup lookup answered the same way would have deleted a **published** release. An asset named
`"<wanted> uploaded <size> <digest>"` used to report its own metadata into the compared columns. The asset list is
now required to be well-formed (length equals expected count, names only `[A-Za-z0-9.-]`, typed state and size,
digest absent-or-sha256) before any of it is parsed. Case-folding narrowed to scheme/host/owner-repo. The un-draft
`PATCH` response is read and checked, with failure text that does not imply nothing shipped. A response's own text
is escaped and truncated before it reaches stdout.

**Not taken, with reasoning (fd76021, SPEC §14):** `target_commitish=<pinned sha>` on the create — see AC #2. The
reviewer's claim that an absent tag lets GitHub create it on the default branch is true; the repair that follows
from the note is not the one proposed.

**Deferred to the canary (TASK-42):** `.target_commitish` as answered by a real create; whether the workflow-write
guard fires for a tag at the head of main; whether `contents: write` from `GITHUB_TOKEN` is sufficient in practice.

**Deferred to TASK-45:** reading `.immutable` after publication, which cannot be tested until immutability is on.

**Harness trap found by the harness itself**, worth repeating: `${var:0:$n,,}` is an arithmetic error in bash that
does not abort a `set -euo pipefail` step, and it made six upload_url cases pass for the wrong reason.

Eighth-round disposition, by commit. **Fixed in 05f224b** (code + harness, each reproduced first): checks accept one answer, so a two-document response fails rather than being judged by its last document; hygiene patterns use \A..\z because jq's Oniguruma ^..$ also matches before a trailing newline; numeric ids are typed (fractional, negative, exponent refused); a declared asset set of 100 or more is refused before create because one full page is not an inventory; and the preflight existence check no longer swallows API failures. **SPEC claims corrected in the same commit** are listed in AC #11. **Still not closeable in YAML**, and SPEC says so: reads and the PATCH do not serialize against another writer, so the controls are the `v*` ruleset (TASK-38), a restricted set of publishers, and immutable releases (TASK-45) — and immutability does not stop a malicious initial publication. A forged response between this run and GitHub is outside any authority boundary the gate has.
<!-- SECTION:NOTES:END -->
