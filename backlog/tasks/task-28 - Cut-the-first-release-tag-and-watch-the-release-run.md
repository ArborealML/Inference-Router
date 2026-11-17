---
id: TASK-28
title: Cut the first release tag and watch the release run
status: Waiting
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T05:58:00Z'
updated_date: '2026-11-17T16:00:00Z'
labels:
  - release
  - ci
dependencies:
  - TASK-50
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
One tag, one run: the release workflow has only ever been exercised as a dry run, and it refuses to publish unless
every declared leg produced a verified archive. decision-10 fixed the surface — linux-x64 and win-x64, nothing else.

**Wake: the owner's `go | hold` on [TASK-50](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-50%20-%20Decision-approve-the-first-public-version-number-v0.1.0-—-one-line.md), and nothing else.** Under decision-16 the cut itself is an
agent act that gets reported; requiring a go-ahead first is decision-21, pending that same ratification. Everything
else here is an agent act, so this task sits Waiting and names that event. A `hold` records a decision and does not
release this task.

**Standing rule — the floor a tag must clear.** Actions runs the workflow from the commit the tag points at, so a tag
on an older commit runs an older, weaker gate: it could publish unchecked bytes, and before decision-10 it also
queued macOS legs no runner services. Never tag a hardcoded sha. Establish the floor and honour it:

```bash
floor=$(git log -1 --format=%H -- .github/workflows/release.yml)
git merge-base --is-ancestor "$floor" "$(git rev-parse origin/main)" && echo ok
git tag -a v0.1.0 -m v0.1.0 "$(git rev-parse origin/main)"   # current main HEAD
git push origin v0.1.0 && gh run watch <id>
```

**Cut:** the releases page gains two archives plus `SHA256SUMS`, [TASK-14](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-14%20-%20Improve-the-README-patterns-from-popular-.NET-repos-plus-an-icon-and-banner.md) can point at real downloads, and
this task reports what the gate proved. **Hold:** the README keeps telling readers to build from source and this task
stays parked. Recommendation, stated not asked: cut `v0.1.0` only after [TASK-44](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-44%20-%20Close-the-open-release-gate-review-findings-SPEC-v0-12-15-onward.md) closes,
[TASK-45](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-45%20-%20Enable-GitHub-immutable-releases-before-the-first-tag.md) is done and the canary on [TASK-42](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-42%20-%20Decision-prove-the-release-gate-on-a-canary-knowing-the-tag-name-is-burned.md) has run — immutable releases freeze whatever ships
first and a published name is never reused.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 One tag run builds and smoke-tests linux-x64 and win-x64 on their own runners before publish
- [ ] #2 The published release carries both binaries plus SHA256SUMS, and the run is read rather than assumed green
- [ ] #3 The README install path points at the published assets
- [ ] #4 the tag points at a commit containing the newest release-gate work, proven by the ancestry check quoted in this task's report
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**What gates this, as events.** [TASK-50](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-50%20-%20Decision-approve-the-first-public-version-number-v0.1.0-—-one-line.md)'s `go`; [TASK-38](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-38%20-%20Act-put-a-v-ruleset-on-the-repository-and-read-it-back.md)'s ruleset read back — a `creation` rule
with empty `bypass_actors` blocks the principal that pushes the tag, so the ruleset and the first tag cannot both be
first; [TASK-45](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-45%20-%20Enable-GitHub-immutable-releases-before-the-first-tag.md) done; [TASK-44](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-44%20-%20Close-the-open-release-gate-review-findings-SPEC-v0-12-15-onward.md) closed; CI green on the tagged commit.

**Preconditions, not criteria:** two-platform legs (TASK-15, closed) and the platform surface (decision-10). Which
commit last raised the floor is history and lives in git and the SPEC revision log; the rule above is the floor, so no
sha quoted here can go stale. Superseded log entries for this task: [board trim, 2026-11-17](https://github.com/ArborealML/Inference-Router/blob/main/backlog/archive/board-trim-2026-11-17.md).

**Duplicate archived:** [TASK-32](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-32%20-%20Decision-cut-the-v0.1.0-tag.md) carried the same deliverable, actor and outcome and is in the archive; its
go/hold analysis now lives on TASK-50, and its `Decision:` title contradicted an agent assignee.
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
