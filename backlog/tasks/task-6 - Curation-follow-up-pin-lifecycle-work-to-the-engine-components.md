---
id: TASK-6
title: 'Curation follow-up: pin lifecycle work to the engine components'
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-16T20:40:00Z'
updated_date: '2026-11-17T16:15:00Z'
labels: []
dependencies:
  - TASK-1
priority: low
ordinal: 6000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
After the native engine-lifecycle work lands, re-scan and make sure the map shows the new provider/registry components and their relationships to EngineRuntime.

**Status notes**
Blocked in practice on TASK-1 (the lifecycle provider): until the provider/registry components exist
there is nothing to pin. When it does run, the map edits go to @claire for approval before commit —
the map is a reviewed artifact, not agent scratch.

Open finding, needs @claire's call before any map edit: the library's eleven components sit directly
under the system (their metadata says `parent: inference-router`), so the rendered map files them under
"Unidentified container" — the scan gives each executable an entry point and core places those in a
container, but the class library has no executable entry of its own, being compiled into both
executables. Probed at 03736f2 in a throw-away worktree: a hand-drafted `router-library` container did
not match any scan finding in that probe (`groma accept router-library` → "no scan match"), and
relocating a curated component is refused (`groma edit routercore --parent router-library` → "cannot
move 'routercore' because it has authored meaning"). No supported in-place migration for curated
components was found in the current tooling, and stripping each component's description to force the
move is not a supported or lossless sequence, so this is a modelling decision first and a tooling
question second.

**Links**
- Map tree edited through the CLI: [groma/systems/inference-router/](https://github.com/ArborealML/Inference-Router/tree/main/groma/systems/inference-router/).
- Library container as declared: [container.md](https://github.com/ArborealML/Inference-Router/blob/main/groma/systems/inference-router/containers/arboreal-inferencerouter/container.md).
- Code the components must name: [Engines/Lifecycle/](https://github.com/ArborealML/Inference-Router/tree/main/src/Arboreal.InferenceRouter/Engines/Lifecycle/) and [EngineRuntime.cs](https://github.com/ArborealML/Inference-Router/blob/main/src/Arboreal.InferenceRouter/Engines/EngineRuntime.cs).
- Claim being pinned to: [SPEC §5.3.1](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md#L420).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Each lifecycle component in the map names the source files it owns: the provider seam, the coordinator, the delegated-exec provider and the Docker provider
- [ ] #2 The relationships from those components to EngineRuntime are on the map, so a reader can follow a switch from the runtime to the provider that performs it
- [ ] #3 Any re-parenting that changes the map model is recorded as a decision record, so the reasoning is reversible without being re-derived
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**Unblocked, and what is actually owed.** The map-model decision landed (TASK-19: the library is a container) and the
lifecycle provider seam with its delegated-exec and Docker providers is on main, so the remaining work is the pinning
this task always asked for: the map must say which files each engine component owns, including the new seam.

**Standing limitation of the tool, found by measurement at 18d7279 and still true:** a declared container is matched to
a scan only when the scanner produces a container fact, and the C# scanner produces those only from application entry
points — so a library never gets one, and a library container stays a permanent ghost in the map. Any pinning plan has
to accept that or work around it rather than treating it as a defect to fix.

Superseded blocked/unblocked history: [board trim, 2026-11-17](https://github.com/ArborealML/Inference-Router/blob/main/backlog/archive/board-trim-2026-11-17.md).
<!-- SECTION:NOTES:END -->
