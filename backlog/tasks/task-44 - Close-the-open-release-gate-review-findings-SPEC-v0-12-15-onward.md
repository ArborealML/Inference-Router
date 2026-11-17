---
id: TASK-44
title: Close the open release-gate review findings (SPEC v0.12.15 onward)
status: Waiting
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T10:52:00Z'
updated_date: '2026-11-17T14:25:00Z'
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
**Wake: a plan review of the amended release-control sequence, dispatched by the coding session after the thirteenth code review closed; the commit review follows when code lands.** Its completion wakes this task. Fixes carried here: a3d3087 (v0.12.18), d8202bd / a8d9a93 / 8f834f6 (v0.12.19), 95410ba / 8f78a73 (v0.12.20), 79a21e0 (v0.12.21). **No tag has ever run this gate — what is left is a decision, not more code, and the decision is now [TASK-49](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-49%20-%20Decision-approve-Actions-runner-minutes-and-a-second-public-repository-for-destructive-tests.md). Do not cut a tag while this task is open.**

**What the twelfth round changed, plainly:** a refusal summary that truncated to a head was deleting the part of a long `upload_url` that distinguishes one address from another, so a cut now keeps head **and** tail with the true length between; containers are named (`<object>`, `<array>`) instead of dumped through `tojson` with no bound; the summary is built with `jq -s` behind a shape guard, so one response yields one summary and empty or multi-document input says `unreadable response`; status extraction runs under `LC_ALL=C`, and the label now says what it is — status-like numbers mentioned in stderr, not decoded causes. Cases: `long_url_prev`, `url_object_prev` (differential pairs run against the revision they replace), `shape_jq_stderr`.

**Rejected finding:** the tenth review's claim that case-folding the owner/repo prefix admits spellings the previous revision refused — the replaced code folded the same prefix (`${got_prefix,,} != ${want_prefix,,}`), deliberately, since SPEC v0.12.15.

**Harness counts are re-measured against `main` at every landing** by this board session (`python3 .local/relgate.py`); the series lives in git, not here. No criterion inside the release-job steps is a line number — name the step or the exact command.
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
- [x] #13 no gh or jq stderr bytes, and no jq value text, reach a release-job log; the site census and the case that pins it are in Notes
- [x] #14 SPEC's dispositions keep naming the release-write race as open rather than closed by gate changes: a release-write principal can publish first, replace assets, or create a second release on the tag between the last read and the un-draft PATCH, and can publish between cleanup's GET and its DELETE; the controls are the `v*` ruleset (TASK-38), a restricted set of release writers, and immutable releases (TASK-45, off today, locking only after publication). a3d3087 neither widens nor closes it
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**Current round (thirteenth code review over 79a21e0), recorded before any fix exists.** Two P2s reproduced at source by the coding session: an outer truncation still deletes the tail of a long `upload_url`, and a string `"false"` prints identically to the boolean `false`, so the refusal summary cannot distinguish a type error from a value. One harness gap, not a code defect: `diag_fields_survive` passes only because its fixture keeps sibling fields short, so it does not prove what its name claims. AC count stays 12/14 until a fix and a fixture that fails without it both exist.

**The census behind AC #13, measured at the current `main`** (re-run: `git show main:.github/workflows/release.yml | grep -c '2>/dev/null'`): 9 sites discard stderr, 6 jq shape guards discard stdout only, 1 discards both, 1 API read captures stderr to a temp file removed by an EXIT trap and reports only the exit code plus status-like numbers, and 1 merge is compiler output (`dotnet publish … -v minimal 2>&1 | tee aot-<rid>.log`). jq leaks a value only for typed operations, truncated to ten characters, and every typed operation in those six programs sits behind a `type` guard — pinned by `shape_jq_stderr`. Text anywhere that says "sanitized stderr" describes the replaced rule.

**Where the older rounds went.** Dispositions and arguments live in SPEC §15 with the revision log (v0.12.15 through v0.12.21); this body cites them by SPEC version and sha instead of reproducing them. Fixed in aa7ad23, 05f224b, a3d3087, d8202bd/a8d9a93/8f834f6, 95410ba/8f78a73, 79a21e0. Rejected finding, still rejected: the case-fold claim — the replaced code folded the same prefix (`${got_prefix,,}`), deliberately, since v0.12.15. Three limits stay limits, not tasks: an undetectable short array, a duplicate key defeating the tag match, and the `< 2^53` integer ceiling rejecting a future id on purpose. The release-write race (AC #14) is never claimable from YAML; its controls are TASK-38 and TASK-45.

**Owed by the coding session, asked for this round:** the §15 rows that let this task's twelve closed criteria retire out of the open body, which is what keeps it inside the 600-word budget.
<!-- SECTION:NOTES:END -->
