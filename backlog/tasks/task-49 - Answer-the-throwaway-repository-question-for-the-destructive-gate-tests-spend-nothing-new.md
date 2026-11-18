---
id: TASK-49
title: >-
  Answer the throwaway-repository question for the destructive gate tests; spend
  nothing new
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T13:56:00Z'
updated_date: '2026-11-18T19:18:00Z'
labels:
  - release
  - security
milestone: m-9
dependencies:
  - TASK-44
priority: high
type: task
ordinal: 49000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Answered and run: one disposable repository, no new spend.** Adopted from the external option analysis and checked
against this repository's measurements. No spend was requested — the owner's line is `we're not doing paid minutes for
this`, and every run is Actions minutes on a public repository, Linux and Windows legs only.

**Where the laboratory had to be: the user account, not the org.** Creating an org repository needs `admin:org`, which this
token lacks (measured 404), so the laboratory is a user-owned public repository. All probes are repository-scoped, so the
semantics transfer; org-level rulesets could not be enumerated, and that stays a stated limit rather than a conclusion.

**What the laboratory proved, at 19:2xZ** (measured, not reasoned): a `creation` rule with `bypass_actors: []` refuses both
the Actions token's tag push (GH013) and the owner PAT, so it would break the release path — the production ruleset omits
`creation`; with `update` + `deletion` only, the token creates a new `v*` tag and cannot force-move a protected one. A
duplicate release on one tag answers `422 already_exists field: tag_name`. **Immutable releases do not protect assets or
the tag ref**: an asset on a published immutable release was deleted and a different file uploaded under the same name, and
the download URL serves the new bytes; with no ruleset a force-push moved a published immutable release's tag. What the
flag does hold is the version name: deleting the release leaves it burned.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 the option analysis landed and its answer is recorded here as adopted, with what this board checked locally
- [x] #2 the answer names where each destructive test runs and the shape of the disposable repository: pinned workflow, both legs, destinations audited
- [x] #3 the spend line is settled: two legs, Linux and Windows, no paid macOS minutes, and no new paid minutes requested
- [x] #4 the laboratory exists with its workflow pinned and settings read back before any destructive case ran, under the user account because org creation needs `admin:org` and this token lacks it
- [ ] #5 it is deleted after the battery — evidence exported and it is archived, but `DELETE` answers 403 for this token, so the delete is a person's act on the task named for it
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**Wake: the laboratory deleted** (AC #5, and the human task cut for it).

**Consequences for the other tasks.** Asset integrity rests on the gate's digest records and the post-publish readback, not
on the immutable flag, which makes the download readback load-bearing. The tag ref's only measured protection is the
ruleset. Two harness lessons: the same-name asset `422` is an ordinary collision and not an immutability guarantee, and
`GET /releases` read read-after-write inconsistent three times — read objects by the id the create response returned.
Raw captures (statuses, bodies, ids, run ids, served-byte comparisons) sit in an untracked working directory.

**Links**: the gate copied into the laboratory ([.github/workflows/release.yml](https://github.com/ArborealML/Inference-Router/blob/main/.github/workflows/release.yml)); spend rule decision-10; the acts that follow on TASK-38, TASK-42 and TASK-48.

Superseded log entries for this task: [board trim, 2026-11-17](https://github.com/ArborealML/Inference-Router/blob/main/backlog/archive/board-trim-2026-11-17.md).
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->

created: 2026-11-17T14:25:00Z

---

Rolled up under the AGENTS.md duplicate rule: it restated analysis that lives on TASK-42 and TASK-38. One owner question survives here — a second public repository — because a new public repository under the organisation is the owner's call; the canary push and the ruleset POST are agent acts under decision-16, each stating its recommendation rather than asking permission.

---

<!-- COMMENTS:END -->
