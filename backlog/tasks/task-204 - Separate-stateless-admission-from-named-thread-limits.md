---
id: TASK-204
title: "Separate stateless admission from named thread limits"
status: Done
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-27'
updated_date: '2026-11-28T11:08:00Z'
labels: []
milestone: m-17
dependencies: []
priority: high
ordinal: 204000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Default HTTP and CLI calls synthesize the shared anon identity, whose default thread cap can serialize unrelated work across providers. Preserve explicit named-thread concurrency and affinity; omitted identity becomes request-scoped and retires with the ticket.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Concurrent omitted-thread HTTP and CLI calls dispatch independently, including different models; no permanent transient thread or cache entry remains.
- [x] #2 Two explicitly shared named-thread requests respect their cap; status identifies the blocking scope.
- [x] #3 SPEC §§5.1, 6 and 11 specify omitted identity, reserved legacy sentinel migration, and compatibility for deliberate named threads.
- [x] #4 Malformed or conflicting identity is refused before queue effects; a deliberately named identity keeps its configured limits.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Only an absent HTTP thread header selects a request-scoped thread; new CLI chat/complete defaults omit it. Explicit names, including legacy anon, retain exact case-sensitive identity and caps. Refuse blank, control, repeated/comma-ambiguous headers and malformed/duplicate CLI options before effects; preserve priority-refusal precedence. Keep the public named library API non-null; only internal HTTP admission accepts absence.

Use an unattached per-request thread and ticket-kind flag, with no registry/publication entry. Diagnostic-label collisions bypass named affinity, priority, counters and all three upstream identity-hint paths. Preserve content-prefix reuse and physical cleanup. Document client omission migration without deployment cutover or reinterpretation of explicit anon.

Capture-backend tests exercise concurrent omitted HTTP and actual CLI chat/complete sends before another provider releases; explicit names/anon still share caps. Pin malformed refusal without effects, equal-label collision, success, queue overflow, queued/active cancellation and no retained transient registration/hints. Cover StatelessAdmission, CLI completion/priority, priority ingress, cache hint, wire, ticket lifetime, queued priority, independent progress and admin fixtures. Update SPEC §§5.1/6/11/14 at v0.12.167 and README.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Independent omitted identities, explicit named caps and malformed header refusal are covered by HTTP and CLI fixtures. Deployment and credential-bound policy remain separate. [Reviewed implementation](https://github.com/ArborealML/Inference-Router/pull/92) is included in [main](https://github.com/ArborealML/Inference-Router/commit/1bf85d3495ab2517a921d78013ffe69bbefb4325). [Hosted qualification](https://github.com/ArborealML/Inference-Router/actions/runs/46112396399) passed 2,341 tests and the Linux native build. The [publication record](../milestones/wave1-implementation-evidence.md) preserves the original review and fixture history.
<!-- SECTION:NOTES:END -->
