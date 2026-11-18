---
id: TASK-17
title: Review, amend and merge the external rewrite of AGENTS.md
status: Done
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-17T01:27:00Z'
updated_date: '2026-11-18T20:42:00Z'
labels:
  - docs
  - process
milestone: m-10
dependencies: []
priority: high
ordinal: 17000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Audited the merged working contract at 945fc69 on the local host. The merge and its content are verified. Historical CI runs and local commands performed later are identified separately.
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
Audit comparing 265d7d6 to 945fc69: only AGENTS.md changed. Backlog and Groma marker blocks match byte-for-byte (1456 and 379 bytes). The backlog config blob is identical. A new scan covered additions to AGENTS.md and messages across all seven PR commits plus the net merge additions. It counted zero absolute home paths, private URLs, credential patterns, private key headers, review request IDs or email addresses. This measures the retained PR history. The original scanner invocation is undocumented.

Rule dispositions: keep SPEC authority and joint revision commits (old lines 3–5 and 89–91), zero-trim AOT and byte-forwarding constraints (32–39), warning policy (25–28), and central dependencies. Freeze admin success/event and error casing as implemented; the older all-PascalCase wording was inaccurate (40–42). Preserve quoted TOML keys. Accept the sample test or inert dry-run on the changed file (43–47): the previous fixed-path command did not validate edits to the other samples.

Keep pre-code plan review, asynchronous bounded commit review, evidence verification and private review records (49–70). Adopt explicit honest test reporting and per-finding disposition. Move lifecycle warnings and preflight ahead of commands. Strengthen hygiene to cover added lines and commit messages. Keep managed instructions intact. Adopt decision-4 kanban definitions and actionable human-task format. Retain focused tests and CI triage; the later decision-66 changes the test executor. No redacted config-output feature or additional no-paste checker was introduced.

A disposable checkout ran both board lists, Groma instructions, task plan and assignee edits, and verified the resulting task view. On the merge checkout, Debug and Release builds had zero warnings. Both full suites passed 141/141, EventRing passed 10/10, and the shipped-sample test passed 1/1. Native linux-x64 publish completed without trim warnings. The config dry-run succeeded. GitHub run listing, PR checks and watching a completed run succeeded. These are post-merge measurements.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
The [merged PR](https://github.com/ArborealML/Inference-Router/pull/1) and 945fc69 remain the reviewable change. The historical merge decision stands.
<!-- COMMENTS:END -->
