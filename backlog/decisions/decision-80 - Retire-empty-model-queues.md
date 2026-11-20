---
id: decision-80
title: Retire empty model queues when their last member leaves
date: '2026-11-20'
status: accepted
---
## Context

An admitted request can leave an empty per-model queue behind after cancellation or dispatch. Reload preserves the queue map, so names that received work in the past can accumulate in memory and appear in queue statistics.

## Decision

Retire the exact queue entry when its last member leaves. A ticket awaiting publication still counts as a member. Remove the retired queue from statistics. If the same model later receives work, its new queue takes a new place after existing queues at equal depth. Preserve the relative order of queues that stay live.

## Consequences

Selection and removal must check queue identity so that a stale selection cannot act on a replacement with the same model name. This bounds retention of empty queues. Simultaneously populated queues remain governed by existing admission rules. TASK-58 owns plan review and verification.
