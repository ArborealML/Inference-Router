---
id: TASK-49
title: >-
  Decision: approve Actions runner minutes, and a second public repository for
  destructive tests
status: Blocked
assignee:
  - '@claire'
created_date: '2026-11-17T13:56:00Z'
updated_date: '2026-11-17T16:01:00Z'
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
**Answer one line, ~2 minutes: `throwaway repo: yes | no`.** This is the only release question that is a person's. Everything else about proving the gate is an agent act with a recorded recommendation.

**What it approves.** A second, **public** repository under the organisation, used once and then deleted, to run the destructive tests: a duplicate release on one tag, tag create/move/delete against a ruleset, tampering with a draft, publish-then-delete of an immutable release. The organisation has 5 repositories today. The alternative is running the same tests in this repository, where a published version **name is reserved permanently** once immutable releases are on — that is the whole cost difference, and it does not buy fewer runner minutes.

**What it does not decide.** Whether a canary runs, or what the `v*` ruleset looks like: those are agent acts on [TASK-42](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-42%20-%20Decision-prove-the-release-gate-on-a-canary-knowing-the-tag-name-is-burned.md) and [TASK-38](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-38%20-%20Act-put-a-v-ruleset-on-the-repository-and-read-it-back.md), with their recommendations and consequences stated there. Runner spend for either path is roughly 20-40 minute-equivalents per run, Linux and Windows legs only (decision-10); the plan's included minutes are not readable with the working token, so that figure is an estimate written once, not maintained.

**Links**: the canary analysis [TASK-42](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-42%20-%20Decision-prove-the-release-gate-on-a-canary-knowing-the-tag-name-is-burned.md); the ruleset act [TASK-38](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-38%20-%20Act-put-a-v-ruleset-on-the-repository-and-read-it-back.md); immutable releases [TASK-45](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-45%20-%20Enable-GitHub-immutable-releases-before-the-first-tag.md); the workflow under test [release.yml](https://github.com/ArborealML/Inference-Router/blob/main/.github/workflows/release.yml).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 `throwaway repo: yes|no` is answered here, and a yes names the repository and its public surface before creation
- [ ] #2 the two agent-owned questions on TASK-42 and TASK-38 carry their recommendation and consequence in their own bodies
- [ ] #3 TASK-32 and TASK-28 name the event that gates them, not this task
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**What each answer unblocks, and where to read more.** (a) gates the canary (TASK-42), immutable releases (TASK-45) and the `v*` ruleset (TASK-38): enabling immutability before the canary answer is what makes the name permanent, so the order matters and no agent will pick it for you. (b) is the difference between learning the negative results somewhere disposable and learning them in public: the duplicate-release refusal, the ruleset's real behaviour against the Actions app, and what deleting an immutable release actually does. (c) decides one ruleset POST or two, and is the reason the first tag (TASK-28) cannot simply go first: a `creation` rule with an empty bypass list would block the tag push that starts a release.

**Cost of the two paid items, plainly:** a canary run and a rehearsal each cost one Actions run of the same two legs (Linux, Windows — decision-10 keeps macOS off the board). Nothing here asks for new paid minutes; it asks which minutes to spend and where.

**Links**
- The release gate the canary would exercise: [.github/workflows/release.yml](https://github.com/ArborealML/Inference-Router/blob/main/.github/workflows/release.yml); its acceptance criteria: [SPEC.md](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md) §14.
- The three parked tasks: [TASK-42](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-42%20-%20Decision-prove-the-release-gate-on-a-canary-knowing-the-tag-name-is-burned.md), [TASK-45](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-45%20-%20Enable-GitHub-immutable-releases-before-the-first-tag.md), [TASK-38](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-38%20-%20Act-put-a-v-ruleset-on-the-repository-and-read-it-back.md).
- The card in (d): [docs/images/air-social-1280x640.png](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/air-social-1280x640.png), with its provenance and measurements in [docs/images/README.md](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/README.md). Rejected candidates B and C are untracked images on the working machine under `the board's untracked card workspace`; committing one costs a render.
- The upload itself, once the card is settled: [TASK-29](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-29%20-%20Upload-the-social-preview-card-and-the-repository-avatar.md).

Superseded log entries for this task: [board trim, 2026-11-17](https://github.com/ArborealML/Inference-Router/blob/main/backlog/archive/board-trim-2026-11-17.md).
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->

created: 2026-11-17T14:25:00Z

---

Rolled up under the AGENTS.md duplicate rule: it restated analysis that lives on TASK-42 and TASK-38. One owner question survives here — a second public repository — because a new public repository under the organisation is the owner's call; the canary push and the ruleset POST are agent acts under decision-16, each stating its recommendation rather than asking permission.

---

<!-- COMMENTS:END -->
