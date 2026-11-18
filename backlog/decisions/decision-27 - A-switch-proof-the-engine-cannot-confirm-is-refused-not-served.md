---
id: decision-27
title: >-
  A switch proof the engine cannot confirm is refused, not served
date: '2026-11-18T01:51:00Z'
status: accepted
---
## Context

When a model switch is configured not to wait for a health rule, the router launches the target and then asks the engine
what it is holding. Until now, if the engine refused to answer, the switch still completed and the request was served,
while the engine's capability was marked unknown — a posture pinned by a test and defended in the provider interface as
a promise made in configuration. Authorising a waiting caller against the capability snapshot at the moment it resumes
cannot coexist with that: the caller would be refused on the wire while the feed said the switch completed. The owner was
asked to choose, and answered with one word: **refuse**.

## Decision

A switch whose confirming observation cannot be made is **not** a proof. The switch fails: it emits the existing
`engine.switch_failed` kind with a reason naming what could not be confirmed, publishes the unknown capability, and ends
the readiness task false. Serving continues for everything the engine can still show it serves; it does not continue for
the model this switch could not confirm. The pinned test moves with this change, in the same commit as the code and the
spec wording.

## Consequences

The router's promise becomes the one it can keep: it serves what it can show, and it refuses the rest loudly. Operators
who relied on the old leniency — a slow engine answering after the fact — now see a refusal and a reason instead of a
request that went out on an unconfirmed engine, so the wording of that reason is load-bearing. It also removes the last
blocking dependency on the switch-reading work: the readiness rule can go in as designed, with the refusal path already
decided rather than inferred.
