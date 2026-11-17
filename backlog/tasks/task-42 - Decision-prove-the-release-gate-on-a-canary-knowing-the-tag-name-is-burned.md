---
id: TASK-42
title: 'Decision: prove the release gate on a canary, knowing the tag name is burned?'
status: Blocked
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T09:53:00Z'
updated_date: '2026-11-17T13:56:00Z'
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
rejected, or the immutability and tag-rule claims in [TASK-45](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-45%20-%20Enable-GitHub-immutable-releases-before-the-first-tag.md) and [TASK-38](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-38%20-%20Decision-protect-release-tags-and-decide-who-may-publish-releases.md) are false.

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
created: 2026-11-17T10:58:00Z

---

Reassigned under decision-16: an agent runs it. Wake: [TASK-44](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-44%20-%20Close-the-open-release-gate-review-findings-SPEC-v0-12-15-onward.md) merged and [TASK-45](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-45%20-%20Enable-GitHub-immutable-releases-before-the-first-tag.md) done, with CI green on the merge commit. Protocol, tightened by the round-8 review: tag current `main` as `v0.0.0-canary`, push, let the unmodified workflow run with its own token, capture the create and upload responses, the full asset inventory, the download SHA-256, the peeled tag SHA, the `PATCH` response, and `.immutable` on the published release; then try to replace an asset and to move the tag and **require both to be refused**; then delete the release with `--cleanup-tag`, because preflight refuses a tag that already carries a release, draft or published. Cost: public assets for a few minutes plus one runner leg. It is a mutation of a public repository and the report says so in those words.

---

created: 2026-11-17T11:06:00Z

---

Hardening from the TASK-39 answer: the canary is also the test bed for consumer verification. On the published canary, run the readback against the live URLs and try the documented user verification path; record the gh version used. Measured here: gh 2.46.0 has none of `gh release verify`, `gh release verify-asset`, `gh attestation` (each exits 1), so if the release README is to tell users those commands, the canary is where the version floor gets established — and if it tells them `sha256sum` plus a REST listing, the canary proves that works too.

---

created: 2026-11-17T11:31:00Z

---

Canary evidence items, from the seventh review's disposition
(aa7ad23, fd76021). Record these from the live run rather than reasoning about them afterwards:
(i) what `.target_commitish` says on the created release — this is the only way to see what the omitted parameter
actually resolved to, and it is the item that replaces the rejected `target_commitish` hardening on TASK-44;
(ii) whether the workflow-write guard fires at all for a tag cut at the head of main — by the endpoint note it
should not, because the resolved target is the default-branch head and that diff is empty by construction, but
our own tag commits touch `.github/workflows/release.yml`, so the answer matters and only a run gives it;
(iii) whether `contents: write` from `GITHUB_TOKEN` suffices for asset upload and the un-draft `PATCH` in practice.

Ownership caution: the canary cannot verify the two repository settings on TASK-45 (immutable releases) and
TASK-38 (a `v*` ruleset) unless those are done **first**. A canary run against an unprotected repository proves
nothing about protection that is not switched on, so the order is 45 and 38, then 42.

---

created: 2026-11-17T13:56:00Z

---

Blocked on TASK-49 (a) and (b): the canary decision and whether the destructive tests run in a throwaway repository. Not Waiting — no event will wake this; a person has to grant it.

---
<!-- COMMENTS:END -->
