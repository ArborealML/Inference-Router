---
id: TASK-49
title: 'Decision: canary yes or no, a throwaway repository, and who may cut a v* tag'
status: Blocked
assignee:
  - '@claire'
created_date: '2026-11-17T13:56:00Z'
updated_date: '2026-11-17T14:00:00Z'
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
**Answer in three lines (~6 minutes), plus one optional. Format: `a) yes|no`, `b) yes|no`, `c) by hand|two rulesets`.** These are permissions and scope calls; every other act on this path is already agent-owned.

**(a) Run a canary release at all — yes or no (~2 min).** The gate has never run against a real tag. The consequence is irreversible: with immutable releases on, a release and its tag can be deleted, but the **version name stays reserved forever**. So a disposable canary is impossible — yes means a public prerelease kept permanently, or a burned name with nothing behind it; no means shipping an untested path. Unblocks TASK-42/45/38.

**(b) A throwaway repository for the destructive tests — yes or no (~2 min).** Duplicate release on one tag; tag create, move and delete against a ruleset; draft tampering; publish-then-delete of an immutable release. Minutes cost the same either way: yes buys the answers without burning a version name here, no runs them in public where names are permanent.

**(c) Who may cut a `v*` tag — one choice (~2 min).** A `creation` rule with no bypass list stops everyone, including the publishing account, from pushing a `v*` tag. `by hand`: one ruleset, release tags cut only by a person. `two rulesets`: creation open to admin and the app, a second forbidding moves and deletions — protects a published release without blocking releases. This decides whether TASK-38 posts one ruleset or two, and why TASK-28 cannot go first.

**(d) Optional (~1 min): keep or swap the committed social card.** It is measured, not eyeballed: 1280x640, 38,150 bytes, smallest text line 42 px against the old card's 20 px. Swapping costs one render now and a second browser trip after TASK-29's upload. Blank means keep it.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 a) is answered yes or no, and the answer names which consequence it accepts: a permanent public prerelease, a burned version name, or an untested release path
- [ ] #2 b) is answered yes or no; yes also opens the throwaway-repository rehearsal, no records that the destructive tests run in this repository and burn names
- [ ] #3 c) is answered `by hand` or `two rulesets`, and TASK-38's criteria are updated to match in the same sitting
- [ ] #4 d) is answered, or left blank to keep the committed card as it stands
- [ ] #5 TASK-38, TASK-45, TASK-42 and TASK-28 are moved off Blocked by the board as soon as the answers land, and any answer that changes a criterion is written into the task rather than a commit message
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
