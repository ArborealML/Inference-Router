---
id: TASK-44
title: Close the open release-gate review findings (SPEC v0.12.15 onward)
status: Waiting
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T10:52:00Z'
updated_date: '2026-11-17T12:55:00Z'
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
**Wake: the eleventh deep review, dispatched over d8202bd..8f834f6** (tier high, asynchronous); its completion
wakes this task. Fixes carried here: a3d3087 (v0.12.18) and d8202bd / a8d9a93 / 8f834f6 (v0.12.19). **No tag has
ever run this gate — that is the open question, a decision on TASK-42 rather than more code. Do not cut a tag while
this task is open.**

**What the tenth round changed, plainly:** the upload address is no longer cut out of the server's `upload_url`.
The run builds it from its own repository context and the validated digits-only id, and the response must still
equal exactly that address — a contract check, not an input. Cases: `built_upload_target` (5 uploads at the
run's own address, 0 at the answer's spelling), `pre_errtoken`, `diag_fields_survive`, `url_wrong_type`.

**Rejected finding, with evidence:** the tenth review claimed case-folding the owner/repo prefix admits spellings
the previous revision refused. The replaced code folded the same prefix (`${got_prefix,,} != ${want_prefix,,}`);
the fold scope has been deliberate since SPEC v0.12.15.

**Three limits stay limits, not tasks** unless we act: a shorter-than-real array in a successful response is
undetectable by exit status or shape; a repeated key defeats the tag match because jq keeps the last value and
duplicate keys are not reported; the integer guard checks jq's printed form, not the wire spelling, and the
`< 2^53` ceiling rejects a future larger id on purpose.

**Harness counts are as-of their commit**, measured by the board session, never inherited: **109 cases, 0 failures,
exit 0 at 8f834f6** (108 at d8202bd, 104 at a3d3087, 90 at 05f224b, 73 at fd76021, 59 at c6c73f7). No criterion
inside the release-job steps is a line number — name the step or the exact command.
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
- [x] #10 the preflight release-existence check fails closed: a release-list read that exits non-zero, answers a non-array body, or carries an unparseable item stops the run rather than reading as no existing release for $TAG (a3d3087; cases pre_partial_page, pre_errdoc_body, pre_junk_item, and the harness runs 05f224b itself answering 'no release' where it should have refused). History kept on purpose: this criterion was checked at 05f224b, the ninth review proved it false for a partial page followed by a failed page, and it is re-checked only now that the repair and the differential case exist
- [x] #11 SPEC's own overstatements are corrected in place rather than kept (05f224b): the stdout claim, the asset state story, and the target_commitish claim as worded in AC #2
- [x] #12 the ninth review's fail-open preflight is repaired and pinned: any failure of the release-list read — a failed page, a non-array body, or a page set that cannot be trusted — stops the run with an explicit error rather than reading as no release, and a harness case runs the previous revision failing open so the hole stays demonstrated rather than remembered
- [ ] #13 every gh and jq invocation whose stderr a step captures passes its text through the same control-character sanitizer before it reaches stdout. Done so far, at d8202bd: the preflight release-list read captures stderr to a file removed by an EXIT trap, caps it at the first line of 2 KiB, replaces credential-shaped runs, then sanitizes (case pre_errtoken: a 40-character token on gh stderr does not reach the log). Still open by design: nobody has swept the remaining steps of the two release jobs for other captured stderr paths, and this criterion stays open until that sweep is recorded here — the board's wording, not the coder's guess, decides the scope
- [x] #14 SPEC's dispositions keep naming the release-write race as open rather than closed by gate changes: a release-write principal can publish first, replace assets, or create a second release on the tag between the last read and the un-draft PATCH, and can publish between cleanup's GET and its DELETE; the controls are the `v*` ruleset (TASK-38), a restricted set of release writers, and immutable releases (TASK-45, off today, locking only after publication). a3d3087 neither widens nor closes it
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

Ninth-round disposition, naming the fix sha. **Repaired in a3d3087**: the release list is judged by gh's exit status rather than by whether a variable came back non-empty, an error body or an unparseable list item is refused with the rule that fired named, and the upload address is compared inside jq — which is what closes the trailing-newline equality above, because shell comparison cannot see the byte that mattered. **Harness counts the uploads** for the non-canonical-id case (5 then, 0 now), which is how the hardening-not-bypass claim is evidenced rather than asserted. **Deliberately not closed**: the sanitizer gap (AC #13) and the release-write race (AC #14). No .NET code changed in this commit, so no dotnet build or test was run.

Tenth-round disposition, by sha. **d8202bd**: the run builds the upload target itself (repository context + validated digits-only id) and the response must equal exactly that address — a contract check, not routing; captured stderr is capped at one line of 2 KiB, credential-shaped runs replaced, then sanitized, and the temp file is removed by an EXIT trap; the create-refusal diagnostic prints one field at a time with the address bounded to 60 characters plus its length. **a8d9a93**: comment and dead variable only — the previous commit had left both describing the rule it had just replaced, which is the kind of stale comment that becomes the next reader's evidence. **8f834f6**: a wrong-typed `upload_url` collapsed the per-field diagnostic to 'unreadable response'; found while writing the next review's payload, not by the reviewer. **Rejected, with evidence:** the case-fold finding above. **Still open:** AC #4 (canary), AC #5 (.immutable, behind TASK-45), AC #13 (the stderr sweep), AC #14 (the release-write race: never claimable as fixed by YAML). No .NET code changed in these three commits, so no dotnet build or test was run.
<!-- SECTION:NOTES:END -->
