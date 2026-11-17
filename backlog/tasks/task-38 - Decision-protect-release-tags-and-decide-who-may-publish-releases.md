---
id: TASK-38
title: 'Decision: protect release tags and decide who may publish releases'
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T09:11:00Z'
updated_date: '2026-11-17T11:38:00Z'
labels:
  - release
  - security
dependencies: []
priority: medium
ordinal: 37000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**An act, not a decision: put a repository ruleset on `v*` that blocks deleting and force-moving release tags,
then read it back and report.** Measured on this repository: `GET .../git/tag-protection/branches` answers 404 —
those endpoints are not available here — while `GET .../rulesets` answers `[]`, so a ruleset is the scriptable route
and it works today. About 20 minutes with `gh api`, reversible with a DELETE.

    gh api repos/ArborealML/Inference-Router/rulesets            # measured [] today

Read the payload shape from GitHub's own ruleset docs before posting rather than guessing it: target `tag`, a
ref_name include of `v*`, and the deletion and non-fast-forward rules. A ruleset that silently matches nothing is
worse than none, so the GET after the POST is part of the act, not a courtesy. It is a mutation of a public
repository: name it in the report and in the commit message.

**Why the board kept pushing:** every review round of the release gate has listed "release-tag protection and who
may publish" as deliberately not closed by YAML — see [.github/workflows/release.yml](https://github.com/ArborealML/Inference-Router/blob/main/.github/workflows/release.yml) and the disposition rows in [SPEC.md](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md) §15.

**Order:** this lands before the canary in [TASK-42](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-42%20-%20Decision-run-a-canary-release-to-prove-the-release-gate-exists.md), because a canary proves nothing about protection that is not switched on.
**Who may publish, stated:** admins plus the Actions token at its least-privilege grant; the token's real sufficiency
is an AC on [TASK-44](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-44%20-%20Close-the-open-release-gate-review-findings-SPEC-v0-12-15-onward.md), not a claim here.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 One line: a tag protection rule is added for the release pattern, or the window is accepted with the reason written into SPEC 15, or access is narrowed instead
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Reasoning and evidence moved out of the description under the answer-first rule (decision-15):

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
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T10:58:00Z

---

Reassigned under decision-16: this was parked as a browser settings act, and it is not one. Measured on this repository: `GET /repos/{owner}/{repo}/git/tag-protection/branches` answers 404, so the tag-protection endpoints are unavailable here; `GET /repos/{owner}/{repo}/rulesets` answers `[]`, so a **repository ruleset** matching `v*` is the scriptable way to get what the review asked for (block delete and non-fast-forward moves on release tags). The ruleset API is a real mutation of a public repository: state the ruleset created, and read it back after. 'Who may publish' stays a written sentence: admins plus the Actions token, least privilege, with the token's own permissions being settled in [TASK-44](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-44%20-%20Close-the-open-release-gate-review-findings-SPEC-v0-12-15-onward.md) AC #4, not here.

---

created: 2026-11-17T11:31:00Z

---

Ownership, settled: as with TASK-45, this is a setting rather than YAML, but it is reachable —
`GET .../git/tag-protection/branches` answers 404 on this repository while `GET .../rulesets` answers `[]`, so a
repository ruleset on `v*` is created over REST with the token this machine already holds (measured
`admin: true`). Agent work under decision-16, created and read back in the same report, and reversible. Not a
prerequisite the canary can fake: TASK-42 proves nothing about tag protection unless this lands first.

---
<!-- COMMENTS:END -->
