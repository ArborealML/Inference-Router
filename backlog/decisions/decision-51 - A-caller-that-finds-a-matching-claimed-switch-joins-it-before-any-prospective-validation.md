---
id: decision-51
title: >-
  A caller that finds a matching claimed switch joins it before any prospective
  validation
date: '2026-11-18T14:55:00Z'
status: accepted
---
## Context

The review of `764a195` found that the entry guard sits ahead of the claim loop, so a caller that should
**join** an in-flight switch can be refused by config state that never governed that switch: A claims on a
valid entry, the entry is removed, B arrives for the same model and is answered `no_select_command` while the
switch A started is still running. Reaching it needs a definition replacement — a test seam, not a boot path
— but the answer a joined caller gets is contract either way.

## Decision

Under the transition gate the order is fixed. Read the switch state; a matching active switch is **joined**
and its completion awaited with no catalog read. Otherwise: the capability check, then resolve and validate
the entry once for that attempt; invalid is a terminal refusal with no `TryPublishSwitchStarted` and no state
write; valid claims and carries that entry down. One absolute deadline spans wait, join and retry.
**Prospective validation never precedes a join.**

## Consequences

TASK-94 #14 carries the two distinguishing pins — a joiner whose entry is removed mid-switch, and a joiner
whose switch then fails — and the absent-entry answer says absent, not unusable. One outcome is selected
under the gate and acted on after release, which keeps the feed emit out of the switch lock (TASK-103 #4). No
feed sequencer: ordering is promised within one transaction, not across callers.
