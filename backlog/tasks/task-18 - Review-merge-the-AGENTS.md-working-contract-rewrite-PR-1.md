---
id: TASK-18
title: 'Review: merge the AGENTS.md working-contract rewrite (PR #1)'
status: To Do
assignee:
  - '@claire'
created_date: '2026-11-17T02:18:00Z'
labels:
  - docs
  - process
dependencies: []
priority: high
ordinal: 18000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The external rewrite of the working conventions was taken as a starting point and amended here. PR #1 is green in CI and carries a rule-by-rule table of what moved, what was put back, and what the rewrite would have lost. The next move is yours: approve and merge, or ask for changes. Nothing in the file is sacred except the tool-managed regions, which are byte-identical.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The rule-by-rule table in the pull request is read against the current file, and anything you want kept that the rewrite narrowed is asked for as a change
- [ ] #2 The pull request is merged or closed with the reason
<!-- AC:END -->

## Do this (about 3 minutes)

1. Open <https://github.com/ArborealML/Inference-Router/pull/1> and read the description's rule-by-rule table, then the **Files changed** tab — the whole
   change is one file.
2. CI is green and the pull request touches only `AGENTS.md`.
3. Either press **Merge pull request**, or leave a comment naming what you want changed.
4. Tell me in one line here: `merged #1`, or `want <x> changed on #1`. I close the dependent task myself.
