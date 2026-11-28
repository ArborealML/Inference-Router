---
id: TASK-207
title: Persist authenticated owned inference thread policy
status: In Progress
assignee:
  - '@gpt-6.2-sol-config'
created_date: '2026-11-27'
updated_date: '2026-11-28T11:55:00Z'
labels: []
milestone: m-19
dependencies:
  - TASK-204
references:
  - src/Arboreal.InferenceRouter.Server/ServerApp.cs
  - src/Arboreal.InferenceRouter/RouterCore.Admission.cs
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
Start with a positively trusted private-only configuration. Inject a verified owner, permitted operations and either stable authorized new-root intent or an existing group association. Keep legacy behavior when the mode is omitted. Neither the reload bearer, headers nor a renamed thread grant root or admin authority. Group-bound stateless calls retain request-scoped admission identity.

Resolve each authorized new root once. Persist scoped association, sensitivity, restrictions and revision before enqueue. Verify concurrent creation, crash boundaries, single writer, missing or corrupt data, and freshness on restore. Refuse unverified rollback or mode removal. Keep I/O outside queue locks. Apply structured owner/group/thread scope through publication, accounting, cache hints and tickets.

Use the same trust, capability and allow/deny predicate through scheduling, preparation and final private send. Validate all senders before startup probes or reload. Disable redirects and proxies on the actual qualified handler, reject unsupported routes and sanitize headers and errors. Deny unclassified and commercial destinations. Test zero egress for probes, inference, retry and auxiliary paths. Preserve finite dispatch and unrelated progress.

Authorize both inference endpoints, catalog/status, tickets, cancel, events, thread controls, switch and reload. Stamp event ownership before replay or publication and scope catalog caching. Keep global actions separately privileged. Test owner collisions, replay after eviction, stale policy and private stream delivery. Production requires proven egress confinement and external root scope. De-escalation needs a separate external privilege or stays disabled. Commercial authorization remains with the commercial guard.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Independent plan review at 3c7ae3e identified default handler redirect and proxy behavior, catalog caching, the use of raw thread IDs, and event ownership after ticket eviction. The revised plan covers each at its enforcement point. TASK-217 owns the external authority choices, TASK-208 owns sensitivity transitions and TASK-212 owns final commercial liability. This plan qualifies fixtures before any production enablement.
<!-- SECTION:NOTES:END -->
