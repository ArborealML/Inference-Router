---
id: decision-69
title: Qualify one landing candidate per deliverable
date: '2026-11-19'
status: accepted
---
## Decision

Use one integration candidate per deliverable. Reuse exact test receipts, scope local reruns to the changed behavior, and review related fixture corrections together. CI owns the full regression suite under the existing verification contract.

## Consequences

The coordinator records which commits and checks each review range covers. Implementers report actionable changes. Routine evidence goes into the next landing.
