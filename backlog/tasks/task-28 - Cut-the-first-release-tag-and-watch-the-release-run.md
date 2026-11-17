---
id: TASK-28
title: Cut the first release tag and watch the release run
status: Waiting
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T05:58:00Z'
updated_date: '2026-11-17T15:01:00Z'
labels:
  - release
  - ci
dependencies:
  - TASK-32
  - TASK-38
  - TASK-39
  - TASK-41
  - TASK-42
  - TASK-43
  - TASK-44
  - TASK-45
  - TASK-48
priority: medium
ordinal: 28000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
decision-10 settled the platform surface: release linux-x64 and win-x64, nothing else. What is left is the first
actual tag run — the release workflow has only ever been exercised as a dry run, and it refuses to publish unless
every declared leg produced a verified archive. Cutting a tag makes a public release; the go-ahead on TASK-32 is a gate check, and this task carries the run once that answer is `cut`.

**Wake condition, current.** TASK-15 landed in 713ffdd and its follow-up fbc48d6 hardened the gate, so the
workflow precondition is satisfied — that is no longer an acceptance criterion, it is a precondition that is true.
The only outstanding wake is the owner's `go | hold` on TASK-50; every other step here is an agent act, so this task sits Waiting and names that event.

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
- Where it appears: [releases](https://github.com/ArborealML/Inference-Router/releases). Decision behind the platform set: [decision-10](https://github.com/ArborealML/Inference-Router/blob/main/backlog/decisions/decision-10%20-%20No-paid-macOS-runner-minutes-release-artifacts-are-Linux-and-Windows-only.md). Owner go-ahead it waits on: [TASK-32] (https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-32%20-%20Decision-cut-the-v0.1.0-tag.md).

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
<!-- SECTION:NOTES:BEGIN -->
Precondition, not an acceptance criterion: the workflow must declare and gate two platforms first (TASK-15). Cut off as a criterion because that change already has an owner and a criterion of its own. Also gated by the owner task asking whether to publish at all: a tag creates a public release, and no decision on the board grants that. Status returns to Waiting when both clear.

Precondition, not a criterion (it is TASK-15 AC #2 and has one owner): the workflow must no longer declare or gate on osx artifacts. Owner authorisation to publish at all is a separate task; TASK-28 is Blocked on it.

Wake condition, updated 07:45 UTC: the TASK-15 precondition is satisfied - commit 713ffdd dropped the osx legs and the gate now counts two archives, re-verified on the checked-out file. What is left is one human act: the go-ahead on TASK-32. That is why this moved from Waiting to Blocked - no event wakes it until a person answers cut or hold.

Floor updated to 6aa26cc for the same reason: the workflow that runs is the one in the tagged commit, so a tag older than 6aa26cc runs the older gate that could pass unchecked bytes. Check-then-tag, never a hardcoded sha as the target.

Floor, stated as a rule instead of a sha so it cannot go stale: the tag must point at a commit that contains the newest release-gate work. Find the floor with `git log -1 --format=%H -- .github/workflows/release.yml` (at the time of writing that is 6b91de4), then check-then-tag: `git merge-base --is-ancestor "$(git log -1 --format=%H -- .github/workflows/release.yml)" "$(git rev-parse origin/main)" && git tag -a v0.1.0 -m v0.1.0 "$(git rev-parse origin/main)" && git push origin v0.1.0`. Actions runs the workflow file from the tagged commit, so a tag behind the floor runs an older, weaker gate.

FIRST-RUN CHECKLIST - the first tag is the only real evidence the gate exists, so it is read by a person and not assumed green. Before pushing: (1) the ancestry rule above passes, and origin/main HEAD is recorded; (2) watch the two build legs and confirm each produced exactly two artifacts plus their expected names. Before un-drafting, from the run log: (3) the captured release id; (4) the tag-to-commit proof line naming the same commit the legs built; (5) the five asset names with state=uploaded and the byte sizes matching the staged files. After publishing: (6) the public URL, the prerelease flag (a tag with a -suffix publishes as a prerelease), the generated notes, and the downloadable SHA256SUMS verified against the archives by a downloader that is not the publishing run; (7) each binary's file type (ELF x64 / PE x64) and a smoke run on its own OS. Failure paths first: exercise them with a dispatch run or a scratch tag that is deleted afterwards, remembering that pushing a tag creates a real release - the workflow refuses a tag that already has a release, published or draft, so a scratch tag must be removed before v0.1.0 is cut.

Three confirmations only a live run can give, REPLACING the three by-tag ones written at 09:16 UTC, which were obsolete before anyone used them (the by-tag endpoint answers only PUBLISHED releases, so a draft this run created is not findable by tag): (1) the create answers with a numeric release id and an upload_url, and those are what the rest of the run addresses; (2) all five assets come back in one snapshot with non-null digests equal to the staged sha256 - that match is also the only current evidence that the upload leg moved bytes faithfully, so a mismatch is the gate working, not a false alarm; (3) the workflow-write guard's real behaviour, which earlier rounds guessed wrong: on create and on update it answers 404, and sometimes 403 with "Resource not accessible by integration" - not 422 - and whether it fires at all when the tag already exists is documented ambiguity rather than settled fact, so the first run is the observation. The other half of the same hazard: if the tag vanished between preflight and create, an omitted target_commitish lets the endpoint create the tag from the DEFAULT branch, so a run could publish a build of a commit nobody chose. Reminder kept: a scratch tag used to exercise failure paths creates a real draft, and preflight then refuses that tag until the draft is deleted.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T13:30:00Z

---

Sequencing trap, from the plan review dispositioned in SPEC v0.12.20 and re-measured by the board at 8f78a73 (0 tags, 0 releases, `[]` rulesets): a `v*` repository ruleset with a `creation` rule and empty `bypass_actors` stops **anyone** — an admin included — from pushing a `v*` tag, so it cannot land before the tag this task cuts unless an actor is listed. Either the ruleset's `bypass_actors` names the tag pusher (TASK-38 AC #2) or this tag is cut first and the ruleset follows. Enforcement has no observe-only setting: `enforcement` is `disabled`, `active`, or Enterprise-only `evaluate`.

---

created: 2026-11-17T15:01:00Z

---

**Waiting, not Blocked:** every assignee here is an agent, so per the revised rule a task that waits on another task sits in Waiting and names it. **Wake:** TASK-50's `go` (or the owner's `ratify decision-21: no`, which removes the gate), plus the `v*` ruleset read back on TASK-38 and TASK-45 done — the ruleset's `creation` rule must be settled before a `v*` tag can be pushed at all.

---
<!-- COMMENTS:END -->
