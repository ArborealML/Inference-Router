---
id: decision-55
title: A measurement series that names a host condition holds the machine
date: '2026-11-18T16:16:00Z'
status: accepted
---
## Context

TASK-87 AC #3 asks for 50 focused and 3 full Release runs on a quiet host with load recorded. Two earlier sets
of counts were discarded because load generators were running — one of them mine — and a whole-suite run at
`0ddd8f3` reported host load 11.0 on 24 cores and qualified for nothing. A series is evidence only while the
machine is idle for its duration, and another seat's build or test run breaks that condition silently: the
series keeps going, and the poisoned runs are indistinguishable in the count.

## Decision

A measurement series **that names a host condition holds the machine** for its duration. No other seat runs a
build, a test run, or anything that holds CPU while it runs; a seat that needs the machine waits for the
series to land. A count taken inside another seat's activity is recorded as void for that criterion, and the
series is re-started rather than patched.

## Consequences

The series owner says what is in flight and records load per run, so a reader can tell a quiet window from a
busy one. Cheap reads stay allowed — git, `gh`, the board checker — but `dotnet` is not cheap: a full Release
suite is precisely the load the criterion measures. Sequencing follows: a heavy measurement lands **before**,
not between, other seats' work on the same host.
