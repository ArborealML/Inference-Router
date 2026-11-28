---
id: TASK-207
title: "Persist authenticated owned inference thread policy"
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-27'
updated_date: '2026-11-28T11:08:00Z'
labels: []
milestone: m-19
dependencies:
  - TASK-204
priority: high
ordinal: 207000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
AIR owns inference-session policy, not identity issuance or agent hierarchy. Consume externally authenticated principal/scope and bind owned thread or policy-group ID, sensitivity and revision in a minimal durable store. Authorized new roots/stateless calls default insensitive; scoped existing sessions cannot escape policy through invented IDs.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Authorized new roots default insensitive; unknown, missing or corrupt existing policy fails closed; unverified restores cannot silently weaken policy.
- [ ] #2 Enforce owner/scope on inference, catalog/status/ticket/cancel/events and privileged admin operations; equal raw IDs never collide across owners.
- [ ] #3 External client/orchestrator associates derived/restored sessions with an authorized policy group; AIR checks scope and refuses forged or omitted IDs from group-bound callers.
- [ ] #4 Allow/deny intersects explicit trust and required capability; deny wins; malformed, empty or unknown restrictions cannot broaden eligibility.
- [ ] #5 Policy evaluator checks queued dispatch, count/tokenize, retries and reload; capture fixtures refuse sensitive commercial candidates. Real legacy transport integration belongs to the send-boundary work.
- [ ] #6 Persistent restart and restore fixtures prove conservative policy handling without prompt storage. SPEC §§5–7, 10, 13–14 specify scope and migration.
- [ ] #7 Owned-policy enablement and rollback refuse reachable unguarded commercial senders or any unclassified destination; before the integrated guard, qualify positively trusted private-only topology with negative egress captures.
- [ ] #8 Existing scoped status exposes effective policy revision, sensitivity, destination trust classification and refusal reason without prompt or credential material.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Reuse simple scoped credentials or verified principal supplied by the deployment. No identity issuer, agent sandbox, agent graph, backup service or general policy engine. Root authorization and optional de-escalation privilege are separate external contracts; AIR checks applicable scope and revision. Private-only production requires proven egress confinement; offline policy fixtures do not. Explicitly classify trusted private providers; locality is not trust. Strip internal auth headers, scope events and sanitize errors. In owned-policy/hybrid mode unknown trust always fails closed; private-only positive trust and policy checks do not require commercial spend reservations. Coordinate final-send contract with TASK-208/210/212.
<!-- SECTION:PLAN:END -->
