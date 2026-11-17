---
id: TASK-38
title: 'Decision: protect release tags and decide who may publish releases'
status: To Do
assignee:
  - '@claire'
created_date: '2026-11-17T09:11:00Z'
updated_date: '2026-11-17T09:11:00Z'
labels:
  - release
  - security
dependencies: []
priority: medium
ordinal: 37000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Why this is yours and not the workflow's.** The release workflow now defends itself against everything it can see:
it refuses a tag that already carries a release (published or draft), re-resolves the tag to a commit and refuses
unless that commit is the one the binaries were built from, and its concurrency group serialises this repository's
own runs. What no line of YAML can do is stop **a person or a process with write access to the repository** from
moving the tag in the few seconds between that check and the create. That is a repository-settings question, and
settings are yours.

**What is at stake, plainly:** the release path publishes whatever the tag points at. A tag moved between the check
and the publish would publish a build nobody reviewed, under a version number people will trust. It is not a
plausible everyday failure — it needs write access and either malice or a careless force-push — but it is the one
remaining hole in a path that has been hardened four times this morning, and it is cheap to close.

**Options, with what each costs:**
1. **A tag protection rule matching the release pattern (for example `v*`)** in the repository's settings. Blocks
   creation and deletion of matching tags except by admins/app designated bypassers. Costs: a few minutes in the
   settings UI; if your plan does not offer tag protection rules, the fallback is option 2. Friction: force-pushing
   or re-cutting a release tag later needs you rather than the agent.
2. **Narrow who has write and admin access** to the repository, and keep releases to one identity. Costs: an access
   review you may not want to do, and it degrades as collaborators arrive.
3. **Accept the window** and record it as an accepted risk, on the grounds that the workflow already re-resolves the
   tag and nobody but you has write access today. Costs: nothing now; it reopens the moment a second person or a bot
   gets write access.

**Recommendation:** option 1 if the plan offers it, with option 3's sentence written down if it does not. Either way
the disposition should be a sentence in the spec's dispositions table rather than silence, because the review
explicitly listed "release-tag protection and who may publish" as *deliberately not closed by code*.

**Answer format (one line):** `tag rule v* added`, or `no rule: accepting the window`, or `narrowing access instead`.
Cost: about 5 minutes, most of it in the settings UI.

**Links** — the settings page: [repository Settings → Rules](https://github.com/ArborealML/Inference-Router/rules)
(tag protection rules live there); the workflow's self-defence: [release.yml](https://github.com/ArborealML/Inference-Router/blob/main/.github/workflows/release.yml);
the review row that raised this: [SPEC §15](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md#L1445),
"Deliberately not closed". The task that cuts the tag: [TASK-28](https://github.com/ArborealML/Inference-Router/tree/main/backlog/tasks).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 One line: a tag protection rule is added for the release pattern, or the window is accepted with the reason written into SPEC 15, or access is narrowed instead
<!-- AC:END -->
