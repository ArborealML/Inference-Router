---
id: TASK-42
title: 'Decision: prove the release gate on a canary, knowing the tag name is burned?'
status: Waiting
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T09:53:00Z'
updated_date: '2026-11-17T14:25:00Z'
labels:
  - release
  - ci
dependencies:
  - TASK-44
  - TASK-45
priority: high
ordinal: 41000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Waiting, and agent-run: no owner keystroke is in this path.** Wake: [TASK-44](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-44%20-%20Close-the-open-release-gate-review-findings-SPEC-v0-12-15-onward.md) closed and [TASK-45](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-45%20-%20Enable-GitHub-immutable-releases-before-the-first-tag.md) done, with CI green on the merge commit. Then the coding session cuts `v0.0.0-canary`, lets the unmodified workflow run with its own token, and reports what the run proved.

**What only a live run answers:** whether `contents: write` from `GITHUB_TOKEN` suffices in practice for asset
upload plus the un-draft `PATCH`; whether the workflow-write guard fires at all for a tag cut at the head of main —
by the endpoint note it should not, and our own tag commits touch release.yml, which is what makes the question
live; what `.target_commitish` reports on a real create; what title and notes body GitHub generates; whether the
stored digests match real uploads.

**Capture, from the run itself:** the create and upload responses, the full asset inventory, the download SHA-256,
the peeled tag SHA, the `PATCH` response, and `.immutable` on the published release.

**Then require refusals:** try an asset replacement and a tag move against the published canary. Both must be
rejected, or the immutability and tag-rule claims in [TASK-45](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-45%20-%20Enable-GitHub-immutable-releases-before-the-first-tag.md) and [TASK-38](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-38%20-%20Act-put-a-v-ruleset-on-the-repository-and-read-it-back.md) are false.

**Cost, plainly:** public assets for a few minutes and one runner leg, and cleanup is mandatory because preflight
refuses a tag that already carries a release, draft or published: `gh release delete v0.0.0-canary --cleanup-tag --yes`. It is a mutation of a public repository and the report says so in those words.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 the decision to run it is recorded (yes, under decision-16); the canary tag `v0.0.0-canary` is cut and the run reported with the unmodified workflow and the job's own token
- [ ] #2 the run answers, as evidence rather than argument, what TASK-41 AC #6 promised: whether `contents: write` from GITHUB_TOKEN is sufficient in practice for asset upload plus the un-draft PATCH, and whether the workflow-write guard fires for a tag cut at the head of main
- [ ] #3 the run records what `.target_commitish` says on a real create, and what the tag object SHA and the peeled commit SHA were
- [ ] #4 the published canary is probed after publication: an asset replacement attempt and a tag move attempt, both required to be refused, plus `.immutable` read back
- [ ] #5 the disposition of the canary is decided before the tag is cut and recorded here: kept as a permanent prerelease, or deleted — in which case the version name stays reserved either way and the report names it. No automatic delete of an uncertain release, and if the rehearsal-in-a-throwaway-repository route is skipped, the reason is written down (it costs no extra runner minutes to rehearse; it saves the burned name)
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**Cost and reversibility, corrected by the plan review (SPEC v0.12.20), and it changes the decision.** With immutable releases on there is no clean undo: deleting the canary's release lets the tag go, but the version **name stays reserved permanently**, and turning immutability off afterwards does not unlock it — so "disposable tag" in this task's title is the premise the review falsified, and the canary is either kept as a permanent prerelease (the review's recommendation) or deleted with the name burned either way. Never auto-delete an uncertain release: cleanup's `GET`-then-`DELETE` window plus a burned name makes a hasty delete worse than a stray draft. **First choice if an extra runner leg is allowed: rehearse in a throwaway repository** — same workflow, same runner minutes, no burned name and no public surface; the rehearsal saves the name and the audience, not the minutes. If the extra leg is refused, record the refusal and run it here. **Also unproven and now stated as unproven in SPEC**: whether `POST /releases` refuses a second release on an existing tag (a `422`) — never tested; and a `v*` ruleset with a `creation` rule and empty `bypass_actors` would block whoever pushes the canary tag itself, so TASK-38's actor list is a precondition of this run.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->

created: 2026-11-17T14:25:00Z

---

Status corrected to match the body: agent-run, no owner keystroke. **Wake:** TASK-44 closed, TASK-38's ruleset read back, TASK-45 done, CI green on the merge commit. Recommendation, stated rather than asked: run the proving tag **after** the throwaway-repository rehearsal if TASK-49 answers yes, and treat the canary as a kept prerelease, because a published version name cannot be reused.

---

<!-- COMMENTS:END -->
