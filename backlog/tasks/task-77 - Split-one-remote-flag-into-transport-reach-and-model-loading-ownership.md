---
id: TASK-77
title: Split one remote flag into transport reach and model-loading ownership
status: Preparing
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-18T02:37:00Z'
updated_date: '2026-11-18T19:20:00Z'
labels:
  - lifecycle
  - scheduling
milestone: m-8
dependencies:
  - TASK-69
priority: high
ordinal: 77000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
One flag answers two questions. remote says both that the router can reach the engine's HTTP API and that the router does not load models into it, which is why a host the router can talk to but cannot start is excluded from switching outright — a cluster host has no way to be switched today. The owner asked for this as a milestone row after the current safety-gate work: decision-25 makes sequenced capacity across hosts the router's goal, and this split is the precondition for any of it. TASK-69 settles the design; this row carries the change that follows from it.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Config states transport reach and lifecycle ownership separately, and remote maps to them with a stated migration
- [ ] #2 An engine the router can reach but cannot start is switchable by a lifecycle provider that speaks to that host
- [ ] #3 The router never stops or launches an engine whose lifecycle it does not own, pinned
- [ ] #4 SPEC documents both flags and says what each one licenses
- [ ] #5 An omitted remote never grants load authority: ownership comes from a target-bound lifecycle provider, never from a hostname or from a missing key
- [ ] #6 A shipped sample engine with no lifecycle block still refuses to switch after the migration, pinned by the sample test
- [ ] #7 A legacy remote endpoint is routable without actuation or adoption. A foreign endpoint sharing a managed host blocks activation until the host authority proves vacancy.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Design direction to test in review, not to assume: two config keys, reach (can the router probe this engine) and lifecycle ownership (may the router start or stop it), with remote mapping to reach-without-ownership for existing configs and a stated migration. A switch would then be licensed by the lifecycle provider rather than by the absence of remoteness, so a provider that talks to a remote host over a transport can drive a switch there, while an engine nobody owns is never touched. Probing, admission and the wait_health posture all read reach, not ownership.

A migration landmine, verified here rather than taken on report: the config loader reads Remote as false when the key is absent, and the shipped sample's hyperqwen entry has no lifecycle or selector block at all. So a migration that treats "remote omitted" as "the router owns this engine" would hand load authority to an OpenAI-compatible endpoint with no controller behind it — today that engine simply fails to switch, which is the safe answer. The normaliser must be presence-aware, and the derived loadability must come from a provider bound to the target.

Owner ruling at 17:31Z and 17:41Z, recorded as decision-56: one engine instance per model, native provider only. The refusal this row owns is measured, not inferred — `EngineRuntime.cs:709` returns `no_lifecycle_owner` for any engine with `remote = true`, ahead of the selector check at `:689` (read at `2b751ec`). Consequence for the bring-up instance: a config whose only engine is `remote = true` cannot switch anything, and a request naming a model that engine declares but does not report is never refused either — it parks, see TASK-51.

The Sol implementer is preparing the loader, runtime and migration plan for the cluster ownership split in an isolated worktree.
<!-- SECTION:NOTES:END -->
