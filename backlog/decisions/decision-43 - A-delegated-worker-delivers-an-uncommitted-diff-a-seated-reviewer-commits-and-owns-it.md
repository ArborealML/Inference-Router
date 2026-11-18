---
id: decision-43
title: >-
  A delegated worker delivers an uncommitted diff; a seated reviewer commits and
  owns it
date: '2026-11-18T08:10:00Z'
status: accepted
---
## Context

Two delegated workers ran in linked worktrees at the owner's request, on their own branches. Neither
could commit: a linked worktree keeps its gitdir inside the parent repository, and the worker sandbox
refuses writes outside its own directory, so the commit attempt failed while creating `index.lock` on
a read-only filesystem. The work itself was useful — three relay tests plus three mutation controls,
built with no warnings and run ten times over — but it existed only as uncommitted files in a worktree
nobody edits. Committing it on the worker's behalf would have meant either this seat writing code
paths it does not own, or inventing a git identity for a process that cannot hold one or answer for it.

## Decision

A delegated worker never commits and never runs `git config`. Its deliverable is an uncommitted diff
in its own worktree, reported with raw command output, counts and a mutation control. A seated
reviewer — the implementer seat, for code and tests — reads the whole diff, re-measures the build and
the focused tests, and commits it under that seat's per-commit identity (decision-29) in the commit
that closes the row's criterion. The board row records the delegation, its wake condition and the
reviewer; it does not credit a worker as the actor that landed anything.

## Consequences

The set of identities in history stays equal to the set of seats, which is what lets the push-range
author-and-committer check (TASK-70) validate a closed list rather than recognise handles on sight.
Feedback to a worker gets slower and blunter: its tests are judged at review, not at commit, and a
report without raw output is not reviewable. A reviewer who commits a worker's diff owns it — the
counts and the mutation control are re-measured before the commit, never copied from the report
(decision-37). A worker whose diff is not committed is a cost, not a result, so rows that delegate say
who will review and merge.

## Alternatives

Let workers commit on their own branches and merge by review (rejected: it needs a gitdir write the
sandbox refuses, and adds an identity per delegation). Keep the board seat as commit-for-hire (rejected:
it breaks the seat split this project exists to keep, and hides who wrote the code).
