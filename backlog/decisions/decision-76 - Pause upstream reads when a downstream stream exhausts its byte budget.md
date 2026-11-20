---
id: decision-76
title: Pause upstream reads when a downstream stream exhausts its byte budget
date: '2026-11-20'
status: accepted
---

## Decision

Count queued bytes and bytes held by a pending flush against a single 262144-byte default cap. Pause upstream reads at capacity and resume when credit returns. This revises the immediate high-water abort in decision 16. Abort with slow_consumer when a pending downstream write makes no progress for 30 seconds, including a one-byte pending write. A healthy burst can therefore drain within the budget. Attribute a downstream pause to the reader when measuring upstream idle. The representative upstream-gap replay and slow-reader soak remain required before shipping defaults.
