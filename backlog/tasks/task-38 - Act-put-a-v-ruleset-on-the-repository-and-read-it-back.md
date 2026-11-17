---
id: TASK-38
title: 'Act: put a v* ruleset on the repository and read it back'
status: Waiting
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T09:11:00Z'
updated_date: '2026-11-17T15:01:00Z'
labels:
  - release
  - security
dependencies: []
priority: medium
ordinal: 37000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**An act, not a decision: create an active repository ruleset on `v*` that governs creating, moving and deleting release tags, then read it back.** Read the live state with the block below before posting; a ruleset is the scriptable route on this repository (the tag-protection endpoint answers 404 for this token), and the answer at the time of the act is what goes in the report. About 20 minutes with `gh api`, reversible with a DELETE.

    gh api repos/ArborealML/Inference-Router/rulesets      # read live, do not quote an older answer

**Three corrections the plan review landed in SPEC v0.12.20, and each one changes the payload.** `enforcement` is `disabled | active | evaluate`, and `evaluate` is Enterprise-only — *create it observe-only, flip it after the canary* is not available: a ruleset enforces or it does nothing. `creation`, `update` and `deletion` are real rule types, and `bypass_actors: []` means **nobody** — not an admin, not the Actions app — may create, move or delete a `v*` tag, which includes whoever pushes the next release tag, so the actor list is decided before the POST and [TASK-28](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-28%20-%20Cut-the-first-release-tag-and-watch-the-release-run.md) is sequenced against it. And a ruleset is not admin-proof: repository admins can always edit the ruleset itself.

**A ruleset that silently matches nothing is worse than none**, so the GET after the POST is part of the act, quoted in the report. This mutates a public repository; say so in the report and the commit message. **Order:** before the canary in [TASK-42](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-42%20-%20Decision-prove-the-release-gate-on-a-canary-knowing-the-tag-name-is-burned.md), which proves nothing about protection that is not switched on.

**Blocked on TASK-49(b): it waits on** the owner's one line about a throwaway repository, because a ruleset must be exercised where destructive tests can run. The rest is agent work — POST, readback, report.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 a repository ruleset exists that targets `v*` with `enforcement: active` and the tag creation/update/deletion rules, and the report quotes `GET repos/ArborealML/Inference-Router/rulesets` after the POST as the readback — not 'tag protection enabled', which this token cannot read (`.../tags/protection` answers 404 at 8f78a73); or the window is accepted with the reason written into SPEC 15, or access is narrowed instead
- [ ] #2 the `creation`-rule scope is decided by the agent and recorded with its reasoning — one ruleset with release tags cut by hand, or two layered rulesets where creation is open to admin and the app and a second forbids moves and deletions — because this is a repository setting reachable over REST (decision-16), not an owner question; whichever is chosen, the body states who can still move a published release's tag afterwards
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
created: 2026-11-17T14:10:00Z

---

Ownership corrected: the ruleset-scope question was parked on the owner by mistake and is now this task's own criterion. The measurement that makes it non-trivial stands — a `creation` rule with `bypass_actors: []` blocks the publishing account as well as a person, so one ruleset either blocks the release path or protects nothing, and two layered rulesets are the shape that does both. The only part that stayed with the owner is the runner spend and the second repository (TASK-49).

---

created: 2026-11-17T15:01:00Z

---

**Waiting, not Blocked:** the ruleset POST is an agent act (decision-16). **Wake:** TASK-49's answer about the throwaway repository, which is where the ruleset gets exercised against the destructive cases.

---
<!-- COMMENTS:END -->
