---
id: TASK-38
title: 'Act: put a v* ruleset on the repository and read it back'
status: Done
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T09:11:00Z'
updated_date: '2026-11-17T19:14:00Z'
labels:
  - release
  - security
dependencies:
  - TASK-49
priority: medium
ordinal: 37000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**An act, not a decision: create a `v*` tag ruleset and read it back.** Read the live state first: a ruleset is the scriptable route here (the tag-protection endpoint answers 404 for this token),  About 20 minutes with `gh api`, reversible.

    gh api repos/ArborealML/Inference-Router/rulesets      # read live, do not quote an older answer

**Three corrections the plan review landed in SPEC v0.12.20; each changes the payload.** `enforcement` is `disabled | active | evaluate`, and `evaluate` is Enterprise-only — *create it observe-only, flip it after the canary* is not available: a ruleset enforces or it does nothing. And a ruleset is not admin-proof: repository admins can always edit the ruleset itself. The third correction — that `bypass_actors: []` means nobody, `creation` included — is what the act then measured and narrowed; the shape it led to is under Notes, and the superseded wording is in the archive.

**A ruleset that silently matches nothing is worse than none:** the GET after the POST is part of the act. This mutates a public repository; say so in the report and the commit message. **Order:** before the canary in [TASK-42](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-42%20-%20Run-the-canary-through-the-real-gate-knowing-the-version-name-is-burned.md), which proves nothing about protection that is not switched on.

**Wake: the disposable repository on TASK-49 existing**, because the ruleset's refusals have to be provable somewhere a tag can be destroyed without spending a real name. The rest is agent work: POST, readback, report.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 A ruleset targeting `v*` with `enforcement: active` exists, and the readback shows a refused tag move and a refused tag delete on an unpublished `v*` tag, causal error quoted — not merely a ruleset that exists
- [x] #2 If access is not widened, the pre-tag window is written as an accepted gap in SPEC 15 instead
- [x] #3 The ruleset's protected operations are decided by the agent and recorded with reasoning: the protection that matters is a published release tag that cannot be moved or deleted
- [x] #4 The report states who can still move a published release's tag, and the `creation` rule's effect on the tag pusher is probed, not assumed
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**What this closes.** The workflow already refuses a tag that carries a release, re-resolves the tag to the built
commit and serialises its own runs. What YAML cannot stop is a principal with write access moving the tag between that
check and the create.

**Two measurements from the act itself change the payload.** (1) **A ruleset with no `conditions` object is
inert:** the first create read back `enforcement: active` and protected nothing — probe pushes moved and then deleted the
canary's tag under it. Adding `conditions: {ref_name: {include: ["refs/tags/v*"], exclude: []}}` by `PUT` (a `PATCH` on
`/repos/{o}/{r}/rulesets/{id}` answers 404) made the same two probes refuse. The list endpoint shows no rules: read back
`GET /repos/{o}/{r}/rulesets/{id}`. (2) **The shape, measured:** rules `update` and `deletion`, no
`creation` rule — creating a new `v*` tag succeeds, moving one and deleting one are refused — so ruleset-then-tag is not a
deadlock. **Measured in the laboratory repository at 19:2xZ:** a `creation` rule refuses the Actions token's tag push (GH013) *and*
the owner PAT (API 422 `Reference update failed`), so it would break the release path; the production ruleset omits
`creation`, and with `update`+`deletion` only the Actions token created a new `v*` tag while its force-move was refused.

Superseded log entries: [board trim, 2026-11-17](https://github.com/ArborealML/Inference-Router/blob/main/backlog/archive/board-trim-2026-11-17.md).

**Order, as performed:** ruleset on `v*` -> immutable releases -> the canary -> post-publish readback proof -> `v0.1.0`.
Read live at 18:2xZ: `release-tag-protection`, target `tag`, `active`, `bypass_actors: []`, conditions `refs/tags/v*`,
rules `update` + `deletion`.

**AC #4 report line.** With `update` + `deletion` and `bypass_actors: []`, the Actions token's force-move of a protected
`v*` tag is refused (GH013) and a tag delete is refused, so no ordinary principal can move a published release's tag: the
route that remains is editing the ruleset itself, which any repository admin holds, and that is the residual risk this
control accepts (immutability does not cover the tag ref — see TASK-45). The `creation` rule was probed in the throwaway
repository, not assumed: it refused the Actions token's push *and* the owner PAT, so production omits it and a new `v*` tag
can still be cut. Evidence: the readback above, and commit d9c3241.
<!-- SECTION:NOTES:END -->
