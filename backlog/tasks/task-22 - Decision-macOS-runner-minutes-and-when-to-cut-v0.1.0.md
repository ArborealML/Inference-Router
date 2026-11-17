---
id: TASK-22
title: 'Decision: macOS runner minutes, and when to cut v0.1.0'
status: Done
assignee:
  - '@claire'
created_date: '2026-11-17T02:18:00Z'
updated_date: '2026-11-17T05:58:00Z'
labels:
  - release
  - ci
dependencies: []
priority: medium
ordinal: 22000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->

The release workflow builds and smoke-tests per-platform binaries; the macOS legs need paid runner minutes enabled, and the release workflow only runs on a tag. Both are yours. Say either "enable macOS minutes and tag v0.1.0 now" or "hold", and TASK-15 and the release task move accordingly. Unblocks TASK-15.

**Do this (about 2 minutes)**
1. **macOS runners**: <https://github.com/ArborealML/Inference-Router/settings/actions> and the billing page. macOS minutes cost more than Linux.
   Say `enable macOS` or `decline macOS`.
2. **First release**: the release workflow only runs on a tag, and it builds four platform binaries and
   smoke-tests each on its own OS before publishing anything. Saying `tag v0.1.0` is enough — I cut the
   tag and watch the run. Say `hold` (and until what) if you would rather not publish yet.

<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Minutes are enabled or explicitly declined
<!-- AC:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T05:55:00Z

---

Kept with @claire deliberately. The macOS half is a spend grant on a paid account, which the working contract names as the canonical human move ("an approval, a scope call, runner minutes"); no agent can authorise it and pretending otherwise would make the board lie about who can act. The **tag** half is agent work and has been split off to TASK-28 so the only thing waiting on a person is the minutes answer.

---

created: 2026-11-17T05:58:00Z

---

Answered by the owner: **no paid macOS runner minutes** — we publish Windows and Linux, and the expectation that macOS was trivial turned out to be true of the port and false of the release proof (a published native binary has to be built and smoke-tested on its own OS, which costs a paid runner, and the x64 macOS image was on a retirement path). AC #1 is checked on that answer. AC #2 (whether to cut v0.1.0) is removed from this Done task because it is a different question with different work behind it, and is carried as TASK-28, whose wake is the workflow stop-claiming-osx work in TASK-15. Recorded as decision-10. Status is Done and the assignee stays @claire as history: the human moved, and that was the whole task.

---
<!-- COMMENTS:END -->
