---
id: TASK-38
title: 'Decision: protect release tags and decide who may publish releases'
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T09:11:00Z'
updated_date: '2026-11-17T13:30:00Z'
labels:
  - release
  - security
dependencies: []
priority: medium
ordinal: 37000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**An act, not a decision: create an active repository ruleset on `v*` that governs creating, moving and deleting release tags, then read it back.** Measured by this session at 8f78a73: `GET .../rulesets` answers `[]`, `GET .../tags/protection` answers 404 for this token, and the repository answers `admin: true`, so a ruleset is the scriptable route. About 20 minutes with `gh api`, reversible with a DELETE.

    gh api repos/ArborealML/Inference-Router/rulesets      # measured [] at 8f78a73

**Three corrections the plan review landed in SPEC v0.12.20, and each one changes the payload.** `enforcement` is `disabled | active | evaluate`, and `evaluate` is Enterprise-only — *create it observe-only, flip it after the canary* is not available: a ruleset enforces or it does nothing. `creation`, `update` and `deletion` are real rule types, and `bypass_actors: []` means **nobody** — not an admin, not the Actions app — may create, move or delete a `v*` tag, which includes whoever pushes the next release tag, so the actor list is decided before the POST and [TASK-28](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-28%20-%20Cut-the-first-release-tag-and-watch-the-release-run.md) is sequenced against it. And a ruleset is not admin-proof: repository admins can always edit the ruleset itself.

**A ruleset that silently matches nothing is worse than none**, so the GET after the POST is part of the act, quoted in the report. This mutates a public repository; say so in the report and the commit message. **Order:** before the canary in [TASK-42](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-42%20-%20Decision-prove-the-release-gate-on-a-canary-knowing-the-tag-name-is-burned.md), which proves nothing about protection that is not switched on. **Who may publish, stated:** admins plus the Actions token at its least-privilege grant; the token's real sufficiency is an AC on [TASK-44](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-44%20-%20Close-the-open-release-gate-review-findings-SPEC-v0-12-15-onward.md), not a claim here.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 a repository ruleset exists that targets `v*` with `enforcement: active` and the tag creation/update/deletion rules, and the report quotes `GET repos/ArborealML/Inference-Router/rulesets` after the POST as the readback — not 'tag protection enabled', which this token cannot read (`.../tags/protection` answers 404 at 8f78a73); or the window is accepted with the reason written into SPEC 15, or access is narrowed instead
- [ ] #2 the bypass question is decided and written before the POST: who is in `bypass_actors` for tag creation, given that an empty list stops the release path itself, and what that means for the order of TASK-28
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

**Measured by this board session at 8f78a73**, with `gh api --jq` and exit codes: `GET repos/ArborealML/Inference-Router` answers `admin: true`; `GET .../rulesets` answers `[]` (length 0); `GET .../tags/protection` answers 404 for this token; the repository has 0 tags and 0 releases; `GET orgs/ArborealML/rulesets` and the org-level immutable-releases setting answer "this API operation needs the `admin:org` scope" — an organisation-wide ruleset or org-level immutability is therefore a person's act, not an agent's, and is not planned here.
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
