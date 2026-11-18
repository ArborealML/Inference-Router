---
id: TASK-28
title: Cut the first release tag and watch the release run
status: Waiting
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T05:58:00Z'
updated_date: '2026-11-18T19:18:00Z'
labels:
  - release
  - ci
milestone: m-9
dependencies:
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

**Wake: the agent predecessors in front matter — TASK-38, TASK-42, TASK-44, TASK-45, TASK-48 — and nothing
else.** Under decision-16 the cut is an agent act that gets reported; decision-21, which would have required a
go-ahead first, was **declined** on 2026-11-17. A `hold` records a decision and does not release this task.

**Standing rule — the floor a tag must clear.** Actions runs the workflow from the commit the tag points at, so a tag
on an older commit runs an older, weaker gate: it could publish unchecked bytes, and before decision-10 it also
queued macOS legs no runner services. Never tag a hardcoded sha. Establish the floor and honour it:

```bash
floor=$(git log -1 --format=%H -- .github/workflows/release.yml)
git merge-base --is-ancestor "$floor" "$(git rev-parse origin/main)" && echo ok
git tag -a v0.1.0 -m v0.1.0 "$(git rev-parse origin/main)"   # current main HEAD
git push origin v0.1.0 && gh run watch <id>
```

**Cut:** the releases page gains two archives plus `SHA256SUMS` and the README can point at real downloads; this task
reports what the gate proved. **Hold:** the README keeps telling readers to build from source. The canary has run,
immutable releases are on, and a published name is never reused — so what is left in front of this tag is gate work, not
a decision.
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
**Ordering fact the graph cannot express.** TASK-48's readback proof can only run against a release that already
exists, so the sequence is: **ruleset on `v*` (protect `update` and `deletion`, leave `creation` free) -> immutable releases
on -> the canary through the real gate -> post-publish readback proof -> the `v0.1.0` tag.** The coding session corrected the order on
2026-11-17: an earlier line here put the canary first, contradicting TASK-42's rule that a canary against an unprotected
repository proves nothing. That order is superseded, not forgotten. TASK-28 depends on TASK-48 for its code and tests, never for a live proof that would need this
tag first; if a criterion ever reads as TASK-48 gating TASK-28 on a published release, that is a deadlock in
prose and gets reworded, not an edge to add.

**What gates this, as events.** [TASK-38](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-38%20-%20Act-put-a-v-ruleset-on-the-repository-and-read-it-back.md)'s ruleset — landed and read back at 18:2xZ:
it protects `update` and `deletion` and leaves `creation` out, so pushing a new `v*` tag succeeds and this tag is not
blocked; [TASK-45](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-45%20-%20Enable-GitHub-immutable-releases-before-the-first-tag.md) done; [TASK-44](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-44%20-%20Close-the-open-release-gate-review-findings-SPEC-v0-12-15-onward.md) closed; CI green on the tagged commit.
Measured since in the laboratory repository: a `creation` rule with empty `bypass_actors` blocks the Actions token and
the owner PAT, so the production ruleset omits `creation` and this tag's push is not blocked.

**Preconditions, not criteria:** two-platform legs (TASK-15, closed) and platform surface (decision-10). Which commit
raised the floor last is history in git and the SPEC log; the rule above is the floor, so no sha here goes stale. Superseded log entries for this task: [board trim, 2026-11-17](https://github.com/ArborealML/Inference-Router/blob/main/backlog/archive/board-trim-2026-11-17.md).

<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
<!-- COMMENTS:END -->
