---
id: TASK-32
title: 'Decision: cut the v0.1.0 tag'
status: To Do
assignee:
  - '@claire'
created_date: '2026-11-17T07:30:00Z'
updated_date: '2026-11-17T08:18:00Z'
labels:
  - release
dependencies: []
priority: medium
ordinal: 31000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
A published release is the owner call, and decision-10 does not grant it: that decision settled which platforms
would be published, not whether anything is published yet. The release workflow has only ever been exercised as
a dry run and the repository has no release.

The predecessor that used to gate this has landed. TASK-15 is Done in commit 713ffdd: the workflow now declares
and gates linux-x64 and win-x64 only, so a tag cut today can close its own asset gate instead of queueing
forever on a macOS leg no runner picks up.

**What changes under each option.** Cut -> the releases page gains linux-x64 and win-x64 archives plus
SHA256SUMS, the README install path can point at real downloads (which is what TASK-14 AC #4 and the release
badge are waiting on), and TASK-28 runs the first real tag and reads the run. Hold -> the repository keeps
telling readers to build from source, which is a legitimate posture: nothing in the router needs a published
binary, and a tag is easy to keep but awkward to un-publish. TASK-28 stays parked and TASK-14 stays open.

**Do this (about 2 minutes).** Answer one line on this task: `cut v0.1.0`, or `hold: REASON`. If the answer is
cut and you would rather not type it, the main session runs it and watches the run.

If you do cut it yourself, cut it on current main HEAD and not on an older commit, because GitHub Actions runs the
workflow file **from the commit the tag points at**: a tag on anything older than fbc48d6 runs the old four-leg
workflow and the macOS jobs queue forever again. Check first, then tag the sha you just checked:

```bash
git merge-base --is-ancestor fbc48d6 "$(git rev-parse origin/main)" && echo ok
git tag -a v0.1.0 -m v0.1.0 "$(git rev-parse origin/main)"
git push origin v0.1.0
```

**Why this task and not TASK-31.** Two identical decision tasks were created two minutes apart. TASK-32 is kept
because it carries an acceptance criterion and the dependency the board reads; TASK-31 had no criterion and is
archived so it cannot collect a second, possibly contradictory answer.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Answer in one line: cut v0.1.0, or hold with a reason
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Floor for the ancestry check updated: the tag must point at a commit containing 6aa26cc (the gate that binds each archive to its producer record), not merely fbc48d6. The rule is a check and then a tag, never a hardcoded target: confirm the floor is an ancestor of origin/main, then tag the origin/main sha you just checked. The floor itself moves forward as the gate hardens, so re-read what the release work needs at the moment you answer, and TASK-36 names the current one.
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
<!-- COMMENTS:END -->
