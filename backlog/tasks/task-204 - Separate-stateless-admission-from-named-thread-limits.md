---
id: TASK-204
title: "Separate stateless admission from named thread limits"
status: Ready for Review
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-27'
updated_date: '2026-11-27'
labels: []
milestone: m-16
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
- [ ] #1 Concurrent omitted-thread HTTP and CLI calls dispatch independently, including different models; no permanent transient thread or cache entry remains.
- [ ] #2 Two explicitly shared named-thread requests respect their cap; status identifies the blocking scope.
- [ ] #3 SPEC §§5.1, 6 and 11 specify omitted identity, reserved legacy sentinel migration, and compatibility for deliberate named threads.
- [ ] #4 Malformed or conflicting identity is refused before queue effects; a deliberately named identity keeps its configured limits.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Only an absent HTTP thread header selects a request-scoped thread; new CLI chat/complete defaults omit it. Explicit names, including legacy anon, retain exact case-sensitive identity and caps. Refuse blank, control, repeated/comma-ambiguous headers and malformed/duplicate CLI options before effects; preserve priority-refusal precedence. Keep the public named library API non-null; only internal HTTP admission accepts absence.

Use an unattached per-request thread and ticket-kind flag, with no registry/publication entry. Diagnostic-label collisions bypass named affinity, priority, counters and all three upstream identity-hint paths. Preserve content-prefix reuse and physical cleanup. Document client omission migration without deployment cutover or reinterpretation of explicit anon.

Capture-backend tests exercise concurrent omitted HTTP and actual CLI chat/complete sends before another provider releases; explicit names/anon still share caps. Pin malformed refusal without effects, equal-label collision, success, queue overflow, queued/active cancellation and no retained transient registration/hints. Cover StatelessAdmission, CLI completion/priority, priority ingress, cache hint, wire, ticket lifetime, queued priority, independent progress and admin fixtures. Update SPEC §§5.1/6/11/14 at v0.12.167 and README.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Candidate 95e800923040584da4e7793747a807536693e221 implements AC1–4. Independent plan and source review e641bee..95e8009 passed after bounded corrections. Exact-head Release verification: 166 passed, zero failed/skipped; build/analyzers passed. Fixtures cover actual HTTP/CLI independent sends, named/anon caps, malformed refusal, queued equal-label priority isolation, affinity/counters/hints, cancellation, overflow and public null rejection.

Prior fc1172e run passed 160/166: six malformed identities returned 500 through the shared mapper. The header-only catch now emits 400; failing assertions remain unchanged. Analyzer/fixture/comment fixes preserve contracts. Base 02087978 passed 608 tests and preserves PR #86. SPEC v0.12.167 describes supported scope. Coordinator next verifies the prepared aggregate, publishes focused PRs and checks hosted build/test/AOT on actual heads. No Done, deployed-client, policy or live qualification claim; Backlog CLI unavailable.

No change to priority, thread weight or provider scoring. TASK-207 owns credential-bound identity and root authorization; transient scheduling identity confers no trust or policy privilege. Architecture §15.3's legacy deployment cutover remains a separate owner choice; preserving explicit anon avoids imposing it here.
<!-- SECTION:NOTES:END -->
