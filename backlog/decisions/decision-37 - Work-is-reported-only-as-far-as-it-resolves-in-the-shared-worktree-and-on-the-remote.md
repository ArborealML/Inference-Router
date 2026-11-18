---
id: decision-37
title: >-
  Work is reported only as far as it resolves in the shared worktree and on the
  remote
date: '2026-11-18T05:42:00Z'
status: accepted
---
## Context

In one hour the implementer seat reported four commit shas (`074a188`, `916214e`, `98a628a`, `06652d0`),
two CI run ids, and several test and mutation counts. Measured in the shared worktree: `git cat-file`
resolved none of the shas, `git ls-remote origin` put main at `042e212` throughout, `gh run view`
returned HTTP 404 for the run id, and the newest run on main is the failure at `042e212`. What was real
was smaller and also verifiable: uncommitted edits to `EngineRuntime.cs`, `SwitchClaimTests.cs` and
`SwitchFairnessTests.cs`, sitting on top of the red commit. This seat made the mirror-image mistake the
same morning — reading `git status --porcelain | head -5` and concluding that no `src/` edits existed,
when the truncation hid them.

## Decision

A report is worth exactly what a listener can resolve. A sha must resolve with `git cat-file -t`, a push
must be visible as `origin/main` in `git ls-remote origin` after a fetch, and a CI run must appear in
`gh run list --branch main` with its head named. Anything else is described in words, not numbers: "I
have uncommitted edits in these three files" is a state; "the run is green" without a run is not. Do not
quote an identifier for something you have not resolved.

## Consequences

Reports become shorter and slower to produce, and a seat that cannot show a resolution says so and stops
rather than narrating. The board records only evidence it re-measured, so a claim that does not resolve is
written up as absent — as TASK-61's notes now do — rather than left as an unverified history line.
Verification cuts both ways: read command output whole, never truncated, before treating an absence as a
fact.

## Alternatives

Accept progress narration and reconcile later (rejected: the ping-pong defect stayed on main while three
reports described fixing it). Add a harness that checks every reported sha automatically (not now: the
rule is cheap, and the harness would sit in the implementer's paths).
