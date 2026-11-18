---
id: TASK-107
title: >-
  Reload the configuration at runtime without restarting the router or dropping
  in-flight requests
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T17:42:00Z'
updated_date: '2026-11-18T19:29:00Z'
labels:
  - config
  - lifecycle
  - serve
milestone: m-8
dependencies: []
priority: high
ordinal: 105000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Owner requirement, relayed 17:38Z: the configuration has to be reloadable while the router runs. Read at `2b751ec`, nothing reloads: no file watcher, no signal handler, no admin reload route (`git grep` for `FileSystemWatcher`, `SIGHUP`, `ReloadConfig` in `src/` returns only the engine's own model-loading paths), and the config is parsed once into immutable domain types at startup. So changing a select block, an engine move, or a ceiling means killing the process and losing the queue — which is also how TASK-77's engine-swap fix would have to be tested today. This row reopens decision-50, which declined an immutable copied engine plan on the rule that the config graph is read-only after boot; reload makes that rule false and the declined option has to be re-argued with revalidation attached.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 One reload trigger is named and works — signal, admin route, or watcher — and a pin drives it end to end
- [ ] #2 SPEC states what reload may change and what still needs a restart, as a table a reader can check
- [ ] #3 A reload landing mid-switch takes effect at a named boundary; no lifecycle transaction sees two configs, pinned
- [ ] #4 A reload that removes an engine with in-flight tickets follows a named rule (drain or refuse), pinned
- [ ] #5 A stream running across a reload completes on the snapshot it started with, asserted on bytes as in TASK-82
- [ ] #6 A reload that fails validation leaves the running config untouched and answers in the single lowercase error shape
- [ ] #7 decision-50 is restated in the same push, its read-only-after-boot premise bounded and the revalidation rule attached
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
TASK-83 covers accepted config keys and runtime handlers. Decision-56 requires native engine selection. Review a portable admin preview/apply with expected generation; classify live, drain and restart-only settings. Keep streams and switches on their starting generation, reject stranded queues, and verify redaction and access. Consider 409 for stale generation and 422 for invalid changes, preserving the old config. Decide the wire contract in review before implementation. A delegated implementer delivers the diff and raw checks; the coordinator integrates. Socket-based proofs require a runner that can bind loopback.
<!-- SECTION:NOTES:END -->
