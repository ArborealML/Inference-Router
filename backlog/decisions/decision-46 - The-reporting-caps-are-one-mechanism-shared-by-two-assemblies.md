---
id: decision-46
title: >-
  The reporting caps are one mechanism shared by two assemblies
date: '2026-11-18T11:05:00Z'
---

# Decision: The reporting caps are one mechanism shared by two assemblies

## Context

The reporting caps (identifier, reason and prose ceilings) became a wire-visible contract with the
feed-bounds work. The ceiling has to be applied in the library, where a terminal request builds its
sentence, and in the server assembly, which writes the HTTP error body. Typing the numbers twice puts
a wire contract in two places, and 8e6867d resolved it by giving `Arboreal.InferenceRouter.Server`
`InternalsVisibleTo` access to the library's `WireText`.

## Decision

The caps are **one mechanism, owned by the library**: the server assembly reads them through
`InternalsVisibleTo` and never re-declares a cap number of its own. That internals grant is exactly
these two product assemblies. A new assembly that wants the caps calls the public surface, or
duplicates a limit **on purpose** and says why in the commit; internals access is not a convenience
for sharing a constant.

## Rationale

Two copies of a limit drift, and these limits are on the wire: an error body that stops being bounded
is a contract break, not a style problem. The boundary that matters is that the product's internals
stay internal to the product, not to one compilation unit. Sharing also keeps the tests honest — one
place to mutate to prove the ceiling is live.

## Consequences

A library-internal rename can now break the server build, which is intended: the caps move together
or the break is loud. The AOT publish at that commit reported no trim warnings (the implementer
seat's report), and the commit review was asked separately whether the visibility grant has any AOT,
trimming or packaging consequence; its disposition belongs with this record. If the caps ever become
a public, documented surface for integrators, that needs an explicit public API rather than a wider
internals list.
