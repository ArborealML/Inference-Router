---
id: decision-3
title: Local testing is focused; CI is the regression gate
date: '2026-11-17T02:09:00Z'
status: accepted
---
## Context

The complete suite was being run before every push, several times per change. On this box that costs
minutes per pass, and under memory pressure `dotnet test` has started dying outright — MSBuild child
nodes fail, or the test host prints nothing at all, which is indistinguishable from a hang and sends
you debugging code that was never broken. Most of those passes re-checked code that had not changed.

## Decision

Run the tests for the area under change while iterating (`dotnet test --filter
FullyQualifiedName~<Area>`). CI runs the complete suite on every push to `main` and is the regression
gate; check its status periodically (`gh run list`, `gh run watch`) instead of reproducing it locally.

A full local run is kept for real boundaries: a change that crosses subsystems (concurrency, engine
lifecycle, the wire contract, AOT-relevant code), and for diagnosis, where a failure has to be seen to
be intermittent rather than assumed.

## Consequences

- Regressions surface in CI rather than at the keyboard, so a push is not the end of the work: the run
  has to be read. CI supersedes an in-flight run when a newer push lands, so the newest run on the
  branch is the one that matters, and a `cancelled` run there is not a failure.
- Fixes that only appear under full-suite load (shared state, timing, memory) are caught a step later
  than before. That is the trade being accepted: it is caught by CI on the same commit, not skipped.
- Less local wall-clock per change, and far fewer false alarms from a test host dying under load.
