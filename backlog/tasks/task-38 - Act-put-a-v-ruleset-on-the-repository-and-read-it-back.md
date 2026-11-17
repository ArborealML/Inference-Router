---
id: TASK-38
title: 'Act: put a v* ruleset on the repository and read it back'
status: Waiting
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T09:11:00Z'
updated_date: '2026-11-17T16:54:00Z'
labels:
  - release
  - security
dependencies: []
priority: medium
ordinal: 37000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**An act, not a decision: create an active repository ruleset on `v*` that governs creating, moving and deleting release tags, then read it back.** Read the live state with the block below before posting: a ruleset is the scriptable route here (the tag-protection endpoint answers 404 for this token), and the answer at the time of the act goes in the report. About 20 minutes with `gh api`, reversible with a DELETE.

    gh api repos/ArborealML/Inference-Router/rulesets      # read live, do not quote an older answer

**Three corrections the plan review landed in SPEC v0.12.20; each changes the payload.** `enforcement` is `disabled | active | evaluate`, and `evaluate` is Enterprise-only — *create it observe-only, flip it after the canary* is not available: a ruleset enforces or it does nothing. `creation`, `update` and `deletion` are real rule types, and `bypass_actors: []` means **nobody** — not an admin, not the Actions app — may create, move or delete a `v*` tag, including whoever pushes the next release tag: the actor list is decided before the POST and [TASK-28](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-28%20-%20Cut-the-first-release-tag-and-watch-the-release-run.md) is sequenced against it. And a ruleset is not admin-proof: repository admins can always edit the ruleset itself.

**A ruleset that silently matches nothing is worse than none**, so the GET after the POST is part of the act, quoted in the report. This mutates a public repository; say so in the report and the commit message. **Order:** before the canary in [TASK-42](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-42%20-%20Run-the-canary-through-the-real-gate-knowing-the-version-name-is-burned.md), which proves nothing about protection that is not switched on.

**Wake: TASK-49's one line** about a throwaway repository, because a ruleset must be exercised where destructive tests can run. The rest is agent work — POST, readback, report.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A ruleset targeting `v*` with `enforcement: active` exists, and the readback shows a refused tag move and a refused tag delete on a `v*` tag — not merely a ruleset that exists
- [ ] #2 If access is not widened, the pre-tag window is written as an accepted gap in SPEC 15 instead
- [ ] #3 The ruleset's protected operations are decided by the agent and recorded with reasoning: the protection that matters is a published release tag that cannot be moved or deleted
- [ ] #4 The report states who can still move a published release's tag, and that empty `bypass_actors` blocks the tag pusher as well
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**What this closes.** The workflow already refuses a tag that carries a release, re-resolves the tag to the built
commit and serialises its own runs. What YAML cannot stop is a principal with write access moving the tag between that
check and the create: the release path publishes whatever the tag points at, under a version number people trust.

**The rule that makes this non-trivial:** a `v*` ruleset with a `creation` rule and `bypass_actors: []` blocks the
publishing account as well as a person, so one ruleset either blocks the release path or protects nothing, and two
layered rulesets are the shape that does both. Measured at 8f78a73; the option analysis and the spend question are on
[TASK-49](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-49%20-%20Decision-approve-Actions-runner-minutes-and-a-second-public-repository-for-destructive-tests.md), not here.

Superseded log entries: [board trim, 2026-11-17](https://github.com/ArborealML/Inference-Router/blob/main/backlog/archive/board-trim-2026-11-17.md).

**Shape, as the coding session's recommendation until readback settles it:** protect `update` and `deletion` on `v*` and do not restrict `creation` — a `creation` rule with `bypass_actors: []` blocks the very principal that pushes the release tag, so a creation rule and the first tag cannot both be first. What must be proven is a refused move and a refused delete on a published tag.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
<!-- COMMENTS:END -->
