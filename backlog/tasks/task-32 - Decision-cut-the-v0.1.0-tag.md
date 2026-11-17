---
id: TASK-32
title: 'Decision: cut the v0.1.0 tag'
status: Waiting
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T07:30:00Z'
updated_date: '2026-11-17T11:38:00Z'
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
**Waiting, not blocked: no person is in this path.** Wake: the canary in [TASK-42](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-42%20-%20Decision-run-a-canary-release-to-prove-the-release-gate-exists.md) green, [TASK-44](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-44%20-%20Close-what-the-seventh-and-eighth-release-gate-reviews-found-over-v0-12-15-and-v0-12-16.md) closed, [TASK-45](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-45%20-%20Enable-GitHub-immutable-releases-before-the-first-tag.md) done. When those land the coding session cuts the tag: the decision content is a gate check, not a preference.

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
Floor for the ancestry check updated: the tag must point at a commit containing 6aa26cc (the gate that binds each archive to its producer record), not merely fbc48d6. The rule is a check and then a tag, never a hardcoded target: confirm the floor is an ancestor of origin/main, then tag the origin/main sha you just checked. The floor itself moves forward as the gate hardens, so re-read what the release work needs at the moment you answer, and TASK-36 names the current one.

Floor restated as a rule, not a sha: cut the tag on origin/main HEAD only after the newest commit that touched the release workflow is an ancestor of that HEAD (find it with git log -1 --format=%H -- .github/workflows/release.yml; 6b91de4 today). The floor moves every time the gate hardens, which it has three times this morning.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T07:45:00Z

---

Kept over TASK-31: identical title, created two minutes later, and this copy carries the acceptance criterion and the board dependency. TASK-31 is archived rather than deleted so the ID and its links survive and a reader can see the duplicate was removed on purpose. Consequences, the one-line answer, the command and the minute cost are now in the description, which is what a person needs to close it without a follow-up question. The TASK-15 gate is gone from the prose because TASK-15 landed in 713ffdd and I re-verified the two-platform workflow myself.

---

created: 2026-11-17T07:53:00Z

---

Added the ancestry step after a review found the sequencing hazard: the workflow that runs is the one in the tagged commit, so a tag older than fbc48d6 would re-run the four-leg workflow and queue macOS jobs that no runner picks up. The check is git merge-base --is-ancestor fbc48d6 origin/main, and the tag is cut on the sha that check just cleared. This is the only reason the owner task now carries a command with three lines instead of one.

---

created: 2026-11-17T09:46:00Z

---

Rewritten to the owner-instruction standard: answer-first block at the top, one line per option, the exact commands, and links that resolve on specific files instead of the tasks folder - this task previously contained zero URLs for a decision about publishing a release. The floor is a command, not a sha, because it has moved five times today.

---

created: 2026-11-17T09:53:00Z

---

Now waits on the canary decision: cutting v0.1.0 on a gate that has never run for real makes the first release the test, which is the wrong order. The canary is the cheap falsifier; this stays a separate yes afterwards.

---

created: 2026-11-17T10:58:00Z

---

Reassigned under decision-16: the cut is an agent act, not an owner's keystroke. Wake: the canary in [TASK-42](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-42%20-%20Decision-run-a-canary-release-to-prove-the-release-gate-exists.md) green, [TASK-44](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-44%20-%20Close-what-the-seventh-and-eighth-release-gate-reviews-found-over-v0-12-15-and-v0-12-16.md) closed, [TASK-45](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-45%20-%20Enable-GitHub-immutable-releases-before-the-first-tag.md) done. The command stays as written in the description — check the floor first, never tag a hardcoded sha: `git log -1 --format=%H -- .github/workflows/release.yml`, `git merge-base --is-ancestor …`, then `git tag -a v0.1.0 -m v0.1.0 "$(git rev-parse origin/main)"` and `git push origin v0.1.0`. Then read the run, do not assume it.

---
<!-- COMMENTS:END -->
