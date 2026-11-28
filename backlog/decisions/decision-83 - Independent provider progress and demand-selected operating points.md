---
id: decision-83
title: Independent provider progress and demand-selected operating points
date: '2026-11-27'
status: proposed
---

## Context

The next-phase architecture review, revision 4, verified the existing dispatcher and fixed-launch Tune planner, and reviewed public provider contracts. The full review is held outside the repository. The current source and SPEC remain authoritative for shipped behavior. The owner requires independently progressing providers, independent context and parallelism, direct commercial providers, and sensitive inference restricted to trusted private providers.

## Decision

Proposed implementation baseline for owner/maintainer review: retain one router writer and existing ordered pending queues. Enumerate available alternatives and complete bounded queue rounds before parking; probes are side-effect-free and preparation is scoped to one physical domain. Preserve queue-only priority, not provider economics, capacity or context policy. Omitted stateless identity is request-scoped, explicit named thread limits remain.

Router selects desired operating points and work; a host manager alone grants verified physical admission, lifecycle mutation and settlement. Combined/router/manager roles use one Native AOT build. Static configured peer placement first; one ingress writer with forwarding, not distributed consensus. Never infer vacancy or non-execution from heartbeat/timeout.

Choose context independently of admission target and physical launch lanes from qualified exact recipes. Use actual finite demand, comparable measured occupancy, transition and cache cost; do not assume lowering admission changes physical allocation. Required ladder is 64K, 96K, 128K, 196K, 256K, 512K, 768K, 1024K (binary K per decision-74), limited by demonstrated support. Cap each normal next-tier search by preceding measured optimum, with explicit exhaustive override; retain per-point evidence. Keep decision-81's one runner. Adopt explicitly; preserve active work and qualified conformance.

Owned thread lineage defaults insensitive at authorized creation; agent escalation is durable, user-only declassification uses a separate authority, and allow/deny filters intersect trust. Final send authorization is ordered with policy changes. Pi is a client; arbitrary agent tool/network activity is outside inference containment.

Use one configuration-defined HTTP transport and shared protocol handlers. Track scoped concurrency/rates, subscription fit and durable monetary liability separately. Missing usage or no output does not prove non-execution. No automatic replay of ambiguity; independent replacement requires separate authorization and liability. Subscription-only requires upstream enforcement; predicted fit is not a guarantee. No paid test or account mutation is authorized here.

## Consequences

This amends the planned scope of decision-25 (independent resource progress without replacing pending order), decision-74 (independent selection within qualified binary tiers), and decision-81 (matched-work occupancy/recipe evidence). Preserve decision-67 physical cleanup and decision-75 frozen configuration. SPEC sections 4–7, 10–14 and future/open-gap sections change with their owning code/tests under AGENTS.md, not by asserting unimplemented behavior here. The milestone and task acceptance criteria are the reconciliation map. Ratification and CLI visibility must be checked before treating this proposed record as binding. Existing completed work and recorded qualification remain intact.
