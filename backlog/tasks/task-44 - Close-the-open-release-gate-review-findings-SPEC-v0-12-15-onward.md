---
id: TASK-44
title: Close the open release-gate review findings (SPEC v0.12.15 onward)
status: Waiting
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T10:52:00Z'
updated_date: '2026-11-17T12:18:00Z'
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
**Wake: the ninth deep review has completed** (over 05f224b, which SPEC's revision log records as v0.12.17), and
its verdict is **do not cut a tag**: no tag has ever run this gate. Open here: the ninth round's repair, plus two
authorization facts no desk can settle. Next wake: the review of the fix commit.
**Do not cut a tag while this task is open.**

**The finding that makes this round different: a criterion the board had checked was false.** AC #10 claimed the
preflight release-existence check fails closed; it does not. A failed page after partial output leaves the read
non-empty, so the could-not-read guard never fires, and a non-array body is discarded by the type filter — the run
reads "no existing release for $TAG", and GitHub allows two releases on one tag. The checkbox stays, unchecked,
with the claim withdrawn in its own words, because a silently deleted criterion is how a false claim survives. The
exact command and the reason are in AC #10, not repeated here.

**Still open, and not fixable by reading harder:** whether `contents: write` from `GITHUB_TOKEN` suffices in
practice for asset upload plus the un-draft `PATCH`, and whether the workflow-write guard fires for a tag cut at
the head of main. TASK-42 carries both as criteria.

Rounds carried here: seventh (v0.12.15), eighth (v0.12.16), ninth (v0.12.17); the title names the SPEC
floor, not a round number.

**Harness counts are as-of their commit:** 90 cases, 0 failures measured by the board session at 05f224b (73 at
fd76021, 59 at c6c73f7). It moves with the repair and will be re-measured, not inherited. No criterion
inside the release-job steps is written as a line number — name the step or the exact command.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 upload_url is compared case-insensitively only over scheme, host and owner/repo, with the path from the release id onward byte-exact; the literal assets{?name,label} suffix is required and cut at the brace; an already-expanded or unexpected query is refused (aa7ad23; case pub_url_path_case proves /Assets refuses)
- [x] #2 the target_commitish hardening is NOT taken. Reasoning as corrected in 05f224b: the workflow-write guard is a function of the RESOLVED target relative to the default branch and the Actions GITHUB_TOKEN can never satisfy it; the field is documented as unused when the tag already exists, so the resolved target is GitHub's and both directions are canary evidence — the earlier claim that omitting it is empty by construction was overstated
- [x] #3 the un-draft PATCH response is read and checked as a release object, and its failure text does not imply nothing shipped (aa7ad23; case pub_patch_answers_draft shows rc!=0 while the release is public)
- [ ] #4 two authorization facts stay unproven and are stated as such: that contents: write from GITHUB_TOKEN suffices in practice for asset upload plus un-draft (docs say yes, no run has shown it), and whether the workflow-write guard fires for a tag cut at the head of main. Both are criteria on the canary in TASK-42
- [ ] #5 when immutable releases are enabled the gate reads .immutable on the published release and records it, and SPEC states what immutability does not lock: drafts, the pre-publish window, title and notes, and the prerelease/latest flags (open behind TASK-45)
- [x] #6 SPEC carries a disposition row for each review naming every finding's disposition, with the revision-log line in the same commit as the code (v0.12.16, v0.12.17 on main)
- [x] #7 every check accepts exactly one answer: a multi-document response fails instead of being judged by its last document — v0.12.16 deleted a PUBLISHED release on a lookup answering draft:false then draft:true, and published on a read-back answering someone else's release first (05f224b; differential pairs hole_cleanup_two_docs, hole_readback_two_docs run against fd76021 itself)
- [x] #8 string hygiene anchors at the true end of input (\A..\z, not ^..$, which also matches before a trailing newline), and numeric ids are typed so fractional, negative and exponent values are refused before interpolation into an upload address (05f224b; pattern cases, no bypass claimed)
- [x] #9 the asset inventory refuses rather than paginates: a declared set of 100 or more is refused before create, because one full page is not an inventory (05f224b)
- [ ] #10 WITHDRAWN, and kept as a checkbox so nobody has to rediscover it: the preflight release-existence check does NOT fail closed. The read is `gh api --paginate "repos/{owner}/{repo}/releases?per_page=100" 2>/dev/null || true` inside the preflight step; a failed page after partial output leaves the value non-empty so the could-not-read guard never fires, and select(type == "array") drops a non-array body, so the result reads as no existing release for $TAG. GitHub permits two releases on one tag, so a run can build a second release on a published tag after a transient failure. Claimed closed at 05f224b; the ninth review falsified it and the board re-checked the source
- [x] #11 SPEC's own overstatements are corrected in place rather than kept (05f224b): the stdout claim, the asset state story, and the target_commitish claim as worded in AC #2
- [ ] #12 the ninth review's fail-open preflight is repaired and pinned: any failure of the release-list read — a failed page, a non-array body, or a page set that cannot be trusted — stops the run with an explicit error rather than reading as no release, and a harness case runs the previous revision failing open so the hole stays demonstrated rather than remembered
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

Ninth round, recorded before the fix lands so the board's position is on the record: the review completed and its preflight finding was independently re-checked against the workflow source before anything was written here. AC #10 is the first criterion on this board that a later review proved false after it had been checked — the check was made against a commit where the code read as failing closed for the case tested, and the untested case (partial output followed by a failed page) fails open. The disposition naming the fix sha will be appended when the coding session hands one over.
<!-- SECTION:NOTES:END -->
