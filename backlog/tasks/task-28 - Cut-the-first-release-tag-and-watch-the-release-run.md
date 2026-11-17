---
id: TASK-28
title: Cut the first release tag and watch the release run
status: Blocked
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T05:58:00Z'
updated_date: '2026-11-17T08:49:00Z'
labels:
  - release
  - ci
dependencies:
  - TASK-32
  - TASK-37
priority: medium
ordinal: 28000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
decision-10 settled the platform surface: release linux-x64 and win-x64, nothing else. What is left is the first
actual tag run — the release workflow has only ever been exercised as a dry run, and it refuses to publish unless
every declared leg produced a verified archive. Cutting a tag makes a public release, so the go-ahead lives on
TASK-32 as a one-line owner answer; this task carries the run once that answer is `cut`.

**Wake condition, current.** TASK-15 landed in 713ffdd and its follow-up fbc48d6 hardened the gate, so the
workflow precondition is satisfied — that is no longer an acceptance criterion, it is a precondition that is true.
The only outstanding wake is the owner answer on TASK-32, which is why this task is Blocked rather than Waiting.

**Sequencing hazard, found by review, and it is real.** Actions runs the workflow file **from the commit the tag
points at**. Tag anything older than fbc48d6 and the OLD four-leg workflow runs, the macOS jobs queue again, and
the run hangs on archives no runner will ever produce. So, before pushing the tag:

```bash
git merge-base --is-ancestor fbc48d6 "$(git rev-parse origin/main)" && echo ok
git rev-parse origin/main                       # tag THIS sha, the current main HEAD
git tag -a v0.1.0 -m v0.1.0 "$(git rev-parse origin/main)"
git push origin v0.1.0
gh run watch <id>
```

The same ancestry line is on TASK-32, because the owner may cut the tag personally.

**Links**
- Workflow a tag runs: [release.yml](https://github.com/ArborealML/Inference-Router/blob/main/.github/workflows/release.yml); preflight refusal at [96-104](https://github.com/ArborealML/Inference-Router/blob/main/.github/workflows/release.yml#L96-L104); asset gate and archive-count check at [216-231](https://github.com/ArborealML/Inference-Router/blob/main/.github/workflows/release.yml#L216-L231); prerelease handling at [238-241](https://github.com/ArborealML/Inference-Router/blob/main/.github/workflows/release.yml#L238-L241).
- Where it appears: [releases](https://github.com/ArborealML/Inference-Router/releases). Decision behind the platform set: [decision-10](https://github.com/ArborealML/Inference-Router/tree/main/backlog/decisions). Owner go-ahead it waits on: [TASK-32](https://github.com/ArborealML/Inference-Router/tree/main/backlog/tasks).

A tag with a `-suffix` publishes as a GitHub prerelease (fbc48d6); `v0.1.0` with no suffix publishes as final.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 One tag run builds and smoke-tests linux-x64 and win-x64 on their own runners before publish
- [ ] #2 The published release carries both binaries plus SHA256SUMS, and the run is read rather than assumed green
- [ ] #3 The README install path points at the published assets
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Precondition, not an acceptance criterion: the workflow must declare and gate two platforms first (TASK-15). Cut off as a criterion because that change already has an owner and a criterion of its own. Also gated by the owner task asking whether to publish at all: a tag creates a public release, and no decision on the board grants that. Status returns to Waiting when both clear.

Precondition, not a criterion (it is TASK-15 AC #2 and has one owner): the workflow must no longer declare or gate on osx artifacts. Owner authorisation to publish at all is a separate task; TASK-28 is Blocked on it.

Wake condition, updated 07:45 UTC: the TASK-15 precondition is satisfied - commit 713ffdd dropped the osx legs and the gate now counts two archives, re-verified on the checked-out file. What is left is one human act: the go-ahead on TASK-32. That is why this moved from Waiting to Blocked - no event wakes it until a person answers cut or hold.

Floor updated to 6aa26cc for the same reason: the workflow that runs is the one in the tagged commit, so a tag older than 6aa26cc runs the older gate that could pass unchecked bytes. Check-then-tag, never a hardcoded sha as the target.

Floor, stated as a rule instead of a sha so it cannot go stale: the tag must point at a commit that contains the newest release-gate work. Find the floor with `git log -1 --format=%H -- .github/workflows/release.yml` (at the time of writing that is 6b91de4), then check-then-tag: `git merge-base --is-ancestor "$(git log -1 --format=%H -- .github/workflows/release.yml)" "$(git rev-parse origin/main)" && git tag -a v0.1.0 -m v0.1.0 "$(git rev-parse origin/main)" && git push origin v0.1.0`. Actions runs the workflow file from the tagged commit, so a tag behind the floor runs an older, weaker gate.

FIRST-RUN CHECKLIST - the first tag is the only real evidence the gate exists, so it is read by a person and not assumed green. Before pushing: (1) the ancestry rule above passes, and origin/main HEAD is recorded; (2) watch the two build legs and confirm each produced exactly two artifacts plus their expected names. Before un-drafting, from the run log: (3) the captured release id; (4) the tag-to-commit proof line naming the same commit the legs built; (5) the five asset names with state=uploaded and the byte sizes matching the staged files. After publishing: (6) the public URL, the prerelease flag (a tag with a -suffix publishes as a prerelease), the generated notes, and the downloadable SHA256SUMS verified against the archives by a downloader that is not the publishing run; (7) each binary's file type (ELF x64 / PE x64) and a smoke run on its own OS. Failure paths first: exercise them with a dispatch run or a scratch tag that is deleted afterwards, remembering that pushing a tag creates a real release - the workflow refuses a tag that already has a release, published or draft, so a scratch tag must be removed before v0.1.0 is cut.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T07:53:00Z

---

Wake condition rewritten, and a new step added from an external review of the release work, checked against the file: Actions runs the workflow from the commit the tag points at, so a tag on anything older than fbc48d6 runs the old four-leg workflow and the macOS legs queue again. Verified on the checked-out HEAD: matrix legs parse to [linux-x64, win-x64]; the gate names the same two in expected_rids (line 216); the archive-count refusal is at 225; prerelease handling at 238-241. The same ancestry check is now on TASK-32 for the case where the owner cuts the tag personally. Two external deep reviews of this area (713ffdd, fbc48d6) are recorded as in flight; per the contract that changes neither status nor assignee, and their request bookkeeping stays out of the board.

---

created: 2026-11-17T08:49:00Z

---

Dependency added: the tag should not be cut while the gate is under review. TASK-28 now waits on the owner go-ahead (TASK-32) and on the release-gate round closing (TASK-37, wake = the review of 6b91de4 plus the SPEC 15 disposition row). The first-run checklist is in Implementation Notes.

---
<!-- COMMENTS:END -->
