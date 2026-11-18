---
id: decision-67
title: Keep physical admission until upstream cleanup finishes
date: '2026-11-18T20:59:00Z'
status: accepted
---
## Context

A scheduler pass can outlive its observation of a ready engine. A request whose caller has already received a timeout can also have a live upstream operation. The response timeout path disposes a late response in a continuation. The dispatch cleanup can release the logical request slot earlier.

## Decision

Before sending, acquire admission for the exact ticket and verified serving instance in the current engine epoch. Closing admission and acquiring it share one atomic ordering. If closure wins, the stale pass leaves its ticket in the queue and writes zero bytes. If admission wins, the drain accounts for its invocation through response disposal. The switch-triggering ticket holds a provisional reservation outside the drain count. Promote it only against the verified destination.

A caller receives cancellation or timeout within its existing bound. Release its logical slot and terminal event once. If the physical send is still live, transfer its permit to cleanup until the operation settles and any late response is disposed. A drain that reaches its bound with an unresolved invocation refuses mutation. A timeout alone gives no evidence that a different instance can occupy the endpoint safely.

## Consequences

TASK-64 tests an admission racing closure and exact-ticket queue commitment. TASK-25 tests an uncooperative send across caller cancellation, drain timeout and late response disposal. TASK-118 supplies instance-bound delivery. TASK-110 supplies host-level exclusive occupancy and restart reconciliation. Existing request statistics retain their logical meaning.
