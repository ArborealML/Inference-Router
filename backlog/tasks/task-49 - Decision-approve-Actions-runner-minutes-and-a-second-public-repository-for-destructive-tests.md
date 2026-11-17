---
id: TASK-49
title: >-
  Decision: approve Actions runner minutes, and a second public repository for
  destructive tests
status: Blocked
assignee:
  - '@claire'
created_date: '2026-11-17T13:56:00Z'
updated_date: '2026-11-17T14:11:00Z'
labels:
  - release
  - security
dependencies:
  - TASK-44
priority: high
type: task
ordinal: 49000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Answer in two lines, about 3 minutes: `a) yes|no`, `b) no repository | yes, throwaway repo`.** Both are approvals only the account owner can give — one spends runner time, the other puts a new public repository under the organisation. Every other act on this path is agent-owned and already scheduled.

**(a) Spend Actions runner minutes to prove the release gate — ~3 min.** The gate has never run against a real tag. A proving run costs roughly 20 to 40 runner-minute-equivalents: two legs, Linux and Windows, no macOS (decision-10). The plan's included minutes are **not readable with this token** — `orgs/ArborealML/settings/billing` and `orgs/ArborealML/plan` both answer 404 — so that figure is an estimate, not a bill. One consequence is irreversible whatever you answer: once immutable releases are on, a published release's **version name stays reserved forever**, even after the release and its tag are deleted. So a proving run here leaves either a permanent public prerelease or a burned name. No does not mean the gate is fixed later: it means the gap is recorded as accepted, in SPEC, not left silent.

**(b) Create a second, throwaway public repository under the organisation for the destructive tests — ~2 min.** Duplicate release on one tag; tag create, move and delete against a ruleset; tampering with a draft; publish-then-delete of an immutable release. All of it is destructive and none of it belongs in a repository people download from. The organisation has 5 repositories today (measured at b214ce6); the new one would be public, named as disposable, and removed when the tests finish. Same runner minutes either way — this answer chooses where the names get burned, not how much time costs.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 a) is answered yes or no, and the answer states which cost it accepts: runner minutes for a proving run, or the release path shipping with the gap recorded in SPEC as accepted
- [ ] #2 b) is answered, and a yes also names the disposable repository and its public surface before anything is created
- [ ] #3 TASK-38, TASK-42, TASK-45 and TASK-28 are moved off Blocked by the board in the same sitting as the answers, with the gate named in each body
- [ ] #4 any declined item leaves a written consequence in SPEC 15 rather than an unowned question
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**What each answer unblocks, and where to read more.** (a) gates the canary (TASK-42), immutable releases (TASK-45) and the `v*` ruleset (TASK-38): enabling immutability before the canary answer is what makes the name permanent, so the order matters and no agent will pick it for you. (b) is the difference between learning the negative results somewhere disposable and learning them in public: the duplicate-release refusal, the ruleset's real behaviour against the Actions app, and what deleting an immutable release actually does. (c) decides one ruleset POST or two, and is the reason the first tag (TASK-28) cannot simply go first: a `creation` rule with an empty bypass list would block the tag push that starts a release.

**Cost of the two paid items, plainly:** a canary run and a rehearsal each cost one Actions run of the same two legs (Linux, Windows — decision-10 keeps macOS off the board). Nothing here asks for new paid minutes; it asks which minutes to spend and where.

**Links**
- The release gate the canary would exercise: [.github/workflows/release.yml](https://github.com/ArborealML/Inference-Router/blob/main/.github/workflows/release.yml); its acceptance criteria: [SPEC.md](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md) §14.
- The three parked tasks: [TASK-42](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-42%20-%20Decision-prove-the-release-gate-on-a-canary-knowing-the-tag-name-is-burned.md), [TASK-45](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-45%20-%20Enable-GitHub-immutable-releases-before-the-first-tag.md), [TASK-38](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-38%20-%20Decision-protect-release-tags-and-decide-who-may-publish-releases.md).
- The card in (d): [docs/images/air-social-1280x640.png](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/air-social-1280x640.png), with its provenance and measurements in [docs/images/README.md](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/README.md). Rejected candidates B and C are untracked images on the working machine under `.local/card-prep/`; committing one costs a render.
- The upload itself, once the card is settled: [TASK-29](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-29%20-%20Upload-the-social-preview-card-and-the-repository-avatar.md).
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T14:10:00Z

---

**Rescoped, because the first version asked the owner for an agent's decision.** It previously carried a third answer about the scope of the `v*` ruleset — who may create a `v*` tag. That is a repository setting reachable over REST, which decision-16 assigns to the agent, so it now lives as a criterion on TASK-38 with its reasoning, and TASK-38 chooses between one ruleset and two layered rulesets itself. A fourth item, about swapping the social-preview card, moved to TASK-46, where a re-render is a one-line instruction to an agent. What stays here is exactly the two things decision-16 and decision-14 leave with a person: runner spend beyond what decision-10 already allows, and creating a new public repository under the organisation.

---
<!-- COMMENTS:END -->
