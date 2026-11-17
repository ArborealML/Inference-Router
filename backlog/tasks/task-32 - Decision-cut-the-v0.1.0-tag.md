---
id: TASK-32
title: 'Decision: cut the v0.1.0 tag'
status: To Do
assignee:
  - '@claire'
created_date: '2026-11-17T07:30:00Z'
updated_date: '2026-11-17T09:53:00Z'
labels:
  - release
dependencies:
  - TASK-42
priority: medium
ordinal: 31000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Do this (2 minutes).** Answer one line on this task: **cut v0.1.0** or **hold: reason**. If you cut it and would
rather not type the commands, the coding session runs the tag and reads the run; if you cut it yourself, use the
check-then-tag lines below.

**What changes under each answer.** Cut -> the releases page gains linux-x64 and win-x64 archives plus SHA256SUMS, the
README can point at real downloads, and [TASK-28](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-28%20-%20Cut-the-first-release-tag-and-watch-the-release-run.md)
runs the first real tag and reports what the gate proved. Hold -> the repository keeps telling readers to build from
source, TASK-28 stays parked, and the release badge in [TASK-14](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-14%20-%20Improve-the-README-patterns-from-popular-.NET-repos-plus-an-icon-and-banner.md)
stays open.

**Command (check, then tag — never tag a hardcoded sha):**

    git log -1 --format=%H -- .github/workflows/release.yml        # the floor: newest release-workflow commit
    git merge-base --is-ancestor "$(git log -1 --format=%H -- .github/workflows/release.yml)" "$(git rev-parse origin/main)" && echo ok
    git tag -a v0.1.0 -m v0.1.0 "$(git rev-parse origin/main)"
    git push origin v0.1.0

**Why the floor matters:** GitHub Actions runs the workflow file stored in the commit the tag points at, so a tag
behind the newest gate work runs an older, weaker gate.

**What you are approving, and what is still open.** The release workflow:
[release.yml](https://github.com/ArborealML/Inference-Router/blob/main/.github/workflows/release.yml). It has never
published anything — no tag has ever been cut — so the first run is the evidence. Two related calls sit beside this
one: [TASK-38](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-38%20-%20Decision-protect-release-tags-and-decide-who-may-publish-releases.md)
(tag protection and who may publish) and [TASK-39](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-39%20-%20Decision-verify-what-a-downloader-receives-or-keep-accepting-that-we-do-not.md)
(whether to pay for download-and-hash or signing).
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
<!-- COMMENTS:END -->
