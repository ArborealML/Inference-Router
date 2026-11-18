---
id: decision-62
title: Quarantine a failed switch target and recover other models under fresh proof
date: '2026-11-18T18:51:00Z'
status: accepted
---
## Context

On a host with four inference lanes and one resident model, a failed activation of A can leave requests for B waiting. A timed retry of A can displace a healthy B a second time. The owner chose target quarantine and independently proven alternate recovery.

## Decision

Latch the exact failed model, serving profile and configuration generation on its engine. Scheduler wakeups and cooldown expiry cannot retry it. An explicit operator reset or a relevant configuration change permits a fresh attempt. Other targets remain eligible.

Each affected A ticket receives one terminal outcome and corresponding feed event. Before the response starts, a failed switch returns 503 with the existing switch_failed category unless the ticket already terminated, for example at its deadline. A committed stream uses its in-band error contract.

A failed preflight preserves an incumbent with valid proof. After a stop, serving B requires a fresh ownership-checked host transaction and verified readiness. Uncertain occupancy prevents launch. This is a separate B request or cohort; a failed A request never silently becomes a B inference.

## Consequences

On a one-resident-engine host, local inference is unavailable between stopping the incumbent and proving a serving target. The per-engine semaphore serializes one coordinator. Docker peer discovery filters by engine label. Both leave other engines on the same physical host outside that gate. TASK-110 supplies the shared host reservation and restart reconciliation. TASK-114 verifies independent recovery on that host after a failed activation. TASK-119 carries these distinctions into the operator walkthrough.

Transient A failures may require intervention. Four lanes remain available to proven workloads within the host capacity limit. Numerical deadlines need measurement. This decision grants neither stream preemption nor permission to change a live engine on this host.
