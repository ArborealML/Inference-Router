---
id: decision-45
title: >-
  A cut marker is a diagnostic distinguisher, not an identity; correlation uses
  the ticket id
date: '2026-11-18T10:06:00Z'
status: accepted
---
## Context

To make two identifiers that cut to the same head read differently in a feed record, the cut marker now
carries an FNV-1a hash of the whole value, rendered as its first eight hex characters — 32 bits. Measured by
reading the shipped code: that is a birthday-scale distinguisher, so two same-head values collide for
roughly one pair in four billion, and a filter value written literally as another value's head-plus-marker
passes the identifier budget unchanged and matches that record with no collision at all. An external review
offered the alternative of minting an opaque identity id for every model, thread and engine name. SPEC §10
had meanwhile been read as promising that identifiers sharing a head "stay distinguishable", which the
mechanism does not deliver.

## Decision

The marker is a diagnostic for a person reading a feed. It is not identity, not an integrity check, and not
access control, and no criterion may treat it as one. Correlation between a request and its events uses
`TicketId`, which is router-generated and deliberately never cut. Feed filters on thread and engine are
excerpt matching, declared as such in SPEC; the ticket filter is exact. A filter value that already looks
like a marker is accepted rather than rejected — a legitimate identifier may literally carry those
characters, and refusing it excludes valid queries while fixing no other collision. No new wire field
carrying an opaque identity for arbitrary model, thread or engine names lands without the owner's decision,
because it adds per-event bytes to a frozen surface.

## Consequences

SPEC says what a filter guarantees and what it does not, and a test can pin an alias rather than being
surprised by one. Anyone needing exact correlation across a cut uses the ticket id, or the whole identifier
from the admin surface, which stays whole. The board does not chase fingerprint collisions as a defect:
the claim that mattered was in the prose, and narrowing the prose is the fix. A future row that wants
byte-exact identity for a bounded name space has to price the wire cost and go to the owner first.

## Alternatives

Reject marker-shaped filter values (rejected: excludes valid queries, and other collisions remain). Mint an
opaque id per value with an assignment map and eviction (rejected for now: correct, but a wire change, extra
bytes per event and per ring entry, and a new lifetime problem — the owner's call, not a seat's). Widen the
fingerprint to 64 bits (rejected: still not unique, costs a fifth of the marker reserve, and would invite the
same overclaim in prose).
