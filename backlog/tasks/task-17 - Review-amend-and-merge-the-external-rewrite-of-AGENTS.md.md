---
id: TASK-17
title: Review, amend and merge the external rewrite of AGENTS.md
status: Done
assignee:
  - '@claire'
created_date: '2026-11-17T01:27:00Z'
updated_date: '2026-11-17T05:23:00Z'
labels:
  - docs
  - process
dependencies: []
priority: high
ordinal: 17000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
An external deep-tier request is producing a rewrite of AGENTS.md as a pull request against main: the file has accreted, repeats its review and backlog rules several times, and buries constraints under rationale. The rewrite is input, not instruction. This task is the review-and-merge decision, taken by whoever has actually run the loop the file describes.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 The PR (or a complete replacement file plus an applicable patch, if the writer could not push) changes AGENTS.md and nothing else
- [x] #2 The two tool-managed regions (backlog and groma markers and their contents) are byte-identical to main
- [x] #3 Every non-negotiable constraint is traced old-to-new, and anything the rewrite dropped or weakened is amended before merge rather than accepted
- [x] #4 Each command in the rewritten file is run on this machine and produces what the file claims it produces
- [x] #5 The hygiene scan is clean on the PR content and backlog/config.yml is untouched by the merge
- [x] #6 The PR is merged and the disposition of every rule change is recorded on the board
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
A deep-tier external rewrite is in flight; it is asked to open a pull request changing only AGENTS.md, to leave the tool-managed regions alone, and to list every rule it moved, merged or reworded. An in-flight external request does not change this task status. Review stance: the rewrite is evidence. Reject it if a constraint was quietly softened, if a command was changed without being run, or if rationale replaced the constraint instead of sitting under it.

The first attempt could not push: GitHub refused branch creation for the integration, so no branch, commit or pull request exists. Its deliverables were left as sandbox files with no artifact manifest, which means they never reached this side — an external "artifacts complete" claim is not evidence of delivery. A second attempt is in flight, asked to deliver the patch, the replacement file and the rule-by-rule trace inline in the response text instead of as files. Until the bytes are here, nothing is applied: the acceptance criteria are unchanged and the merge stays blocked on them.

Blocked on TASK-18 — the next move is pull request review and merge, which belongs to a human. Work here resumes when that task is closed.

Deep review of the first two commits: not safe to merge, six findings, five correct against this code and fixed in the follow-up commit — the casing rule overstated what is PascalCase (errors are lowercase everywhere and are a wire break either way), the stale-binary warning was stated as a property of a command CI runs safely rather than of building Debug and testing Release, sample loading was credited to the dry-run when a test loads all three shipped samples, accepted gaps were pointed at the acceptance-criteria section instead of the dispositions section, and the rule had lost who performs the review. Its blocker (the kanban section describing columns the branch config did not carry) was resolved by merging main, where the status list lives. Its ordering point is taken: the lifecycle footgun and the tool preflight now come before the commands, and the hygiene rule names the config dry-run output as something never to paste into public text. A review of the pinned head is in flight. Still blocked on TASK-18.

Re-review of the pinned head: still not merge-ready, twice right again. The stale-binary rule named the wrong configuration (plain --no-build judges Debug, so the advice could have kept an agent testing old code), and the casing rule cited the future-commitments section instead of the HTTP surface section while overstating what the tests pin. Both fixed. The review confirms no rule was lost in the rewrite, the five kanban statuses are real on the branch, and the ordering fix puts the footgun before the commands. Remaining review preferences, deliberately not taken: redacted-by-default config output and a machine gate for the no-paste rule are features, not doc fixes; "operator" is left undefined because the file is read by whoever is working. Head is CI-green, AGENTS.md-only, managed regions byte-identical; ACs 1-5 are met and the merge (AC #6) waits on the human review task.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T05:23:00Z

---

Closed on the merge. AC #6 verified in two halves: the pull request is MERGED (merge commit 945fc69, 2026-11-17T04:36:17Z, single file AGENTS.md, tool-managed regions byte-identical, CI green on the merge push), and the disposition of each contested rule change is recorded in the notes above — three external review passes, each finding either fixed in the follow-up commit or declined with the reason (redacted-by-default config output and a machine gate for the no-paste rule are features, not doc fixes; "operator" left undefined on purpose). Where each *unchanged* rule landed is in the pull request description's rule-by-rule trace, which is public repository text, not board text: the board carries the contested dispositions, the merged file carries the outcome. Assignee is now the owner, who performed the merge.

---
<!-- COMMENTS:END -->
