---
id: TASK-32
title: 'Decision: cut the v0.1.0 tag'
status: Waiting
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T07:30:00Z'
updated_date: '2026-11-17T14:48:00Z'
labels:
  - release
dependencies:
  - TASK-42
  - TASK-44
  - TASK-45
priority: medium
ordinal: 31000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Waiting, not blocked: no person is in this path.** Wake: the canary in [TASK-42](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-42%20-%20Decision-prove-the-release-gate-on-a-canary-knowing-the-tag-name-is-burned.md) green, [TASK-44](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-44%20-%20Close-the-open-release-gate-review-findings-SPEC-v0-12-15-onward.md) closed, [TASK-45](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-45%20-%20Enable-GitHub-immutable-releases-before-the-first-tag.md) done. When those land the coding session cuts the tag: the decision content is a gate check, not a preference.

**Cut:** the releases page gains linux-x64 and win-x64 archives plus SHA256SUMS, the README can point at real
downloads (the last open box on [TASK-14](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-14%20-%20Improve-the-README-patterns-from-popular-.NET-repos-plus-an-icon-and-banner.md)), and [TASK-28](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-28%20-%20Cut-the-first-release-tag-and-watch-the-release-run.md) reports what the gate proved.
**Hold:** the README keeps telling readers to build from source and TASK-28 stays parked.

**Command — check the floor first, never tag a hardcoded sha:**

    git log -1 --format=%H -- .github/workflows/release.yml
    git merge-base --is-ancestor "$(git log -1 --format=%H -- .github/workflows/release.yml)" "$(git rev-parse origin/main)" && echo ok
    git tag -a v0.1.0 -m v0.1.0 "$(git rev-parse origin/main)"
    git push origin v0.1.0

Actions runs the workflow stored in the commit the tag points at, so a tag behind the newest gate work runs an
older, weaker gate. Then read the run rather than assuming it: cost after the wake is about 10 minutes plus one
runner leg.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Answer in one line: cut v0.1.0, or hold with a reason
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**Adopted under decision-16 and decision-21:** the cut itself is an agent act, and a public version number needs the owner's one-line go-ahead, which is what waits here. Recommendation, stated not asked: cut `v0.1.0` only after TASK-44 closes, TASK-45 is done and the canary has run, because immutable releases freeze whatever ships first and a published name is never reused.

**Wake:** canary green on TASK-42, TASK-44 closed, TASK-45 done, CI green on the merge commit.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->

created: 2026-11-17T14:25:00Z

---

Status corrected: the body's first line has always said this is agent-run with no person in the path, so Blocked was a contradiction. **Wake:** the canary reported green on TASK-42, TASK-44 closed and TASK-45 done.

---

<!-- COMMENTS:END -->
