---
id: decision-50
title: The switch snapshot is an entry reference, not a private copy of config
date: '2026-11-18T14:47:00Z'
status: accepted
---
## Context

A plan review asked for an immutable copied plan — executable, args, env, profile — so that no later in-place
write could split the check from the use. Reads at `0ddd8f3`: the only write to the config graph anywhere in
`src/` is `RouterConfigLoader.cs:117` (`cfg.Engines = dict`); `_config.Engines` is read at
`EngineRuntime.cs:685` (the switch path) and `:1249` (`CanonicalModel`, off it); no assignment to `.Catalog`,
`.Select`, `.Exec` or `.Profile` exists in `src/`. My first grep for that missed indexer writes and was
re-run, including indexers, before this record was kept.

## Decision

Declined. The snapshot stays a reference to the resolved entry, and the invariant is carried as a stated
rule: **the config graph is read-only after boot and the loader is its only writer**. A second
representation of every engine field on the request path is a drift risk bought against a mutation the
router does not perform.

## Consequences

The rule is stated, not structurally enforced. A config reload, or any admin write to the graph, reopens
this decision and has to carry revalidation with it. Tests may mutate the dictionary through a seam — the
swap pin does exactly that — because production does not; a mutation reached only through a test seam is not
evidence of a production route.
