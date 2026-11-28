---
id: TASK-207
title: Persist authenticated owned inference thread policy
status: Preparing
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-27'
updated_date: '2026-11-28T11:21:00Z'
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
Add an opt-in private-only policy mode that consumes an authenticated principal and independent-root or existing-group scope from the deployment. Treat untrusted request headers as ordinary input. An explicit authorized root creation may establish an insensitive group. Resolve derived, restored and stateless calls without granting authority to create a new root by changing an ID. Use structured owner/group/thread keys and opaque external identifiers. Omitted mode preserves existing behavior.

Persist minimal policy metadata and revision before acceptance. Establish an explicit initialization path and distinguish absence of a previously established record from authorized creation. Reject corrupt or ambiguous state. Never persist prompts, backend credentials or stream content. Share the evaluator across ordinary inference, queued dispatch, discovery, auxiliary calls and retries. Intersect capabilities, positive destination trust and allow/deny restrictions. Refuse unclassified or guardless commercial routes on startup, reload and rollback while owned policy is enabled.

Enforce ownership on named and stateless admissions, catalog, tickets, cancellation, replay/live events and thread views. Reserve global aggregates, switch and configuration actions for independently privileged callers. Test cross-owner identical IDs, forced reload, restart, forged root/restore and zero egress to untrusted capture endpoints. Keep authenticated deployment integration, sensitivity transitions and commercial authorization with their existing owners. Document the supported opt-in scope in SPEC and README.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Source preparation found raw thread keys in RouterCore and separate bearer checks only on the reload surface. Independent plan review was requested for legacy-mode compatibility, scoped reads and event replay, restart durability, missing-policy behavior and private-only egress confinement. Deployment identity issuance and production credential selection remain external.
<!-- SECTION:NOTES:END -->
