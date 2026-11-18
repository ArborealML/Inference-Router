---
id: decision-49
title: A switch transaction also resolves its catalog entry before the claim
date: '2026-11-18T14:30:00Z'
status: accepted
---
## Context

v0.12.44 unified the definition object, but each side still performed its own `def.Catalog.TryGetValue` at a
different moment — pre-claim and inside `RunSwitchAsync` (`:695`, `:1021` at `0dafcd2`). Two asks of a
mutable dictionary: the same mistake one level down, and it made "one predicate, asked once" true of the
definition only. The record review of that commit found it after the code had already been fixed once, which
is why it is a decision and not a footnote.

## Decision

The target's `EngineModelDefinition` is resolved by that same read in front of the claim marker and carried
into the transaction. Where the entry is absent, the answer is a pre-claim refusal — no marker, no
publication, last proof intact — and the post-claim missing-entry branch is deleted rather than re-guarded.

## Consequences

At `0ddd8f3` `:700` is the only entry lookup on the switch path and `:803` hands that entry to
`RunSwitchAsync`. The read is also the mutation policy: a configuration edit landing after it changes no
switch already claimed from it (§5.5). A swap pin therefore has to name the selector handed to the provider —
a launch count is the same answer on both routes. Moving the refusal in front of the claim is what created
the joined-caller problem decision-51 answers.
