---
id: TASK-6
title: 'Curation follow-up: pin lifecycle work to the engine components'
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-16T20:40:00Z'
updated_date: '2026-11-17T06:05:00Z'
labels: []
dependencies:
  - TASK-1
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

Decision needed: what is a container here. The C4 guidance treats a shared assembly as *not* a container,
which argues for keeping the shared components unfiled until we choose a simplification, or for filing
them under the process that actually hosts them — noting that `air serve` runs the HTTP host inside the
CLI process and `air config` loads configuration locally, so the CLI is not merely a remote client of the
library and the map cannot show one source file owned by both executables.

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
Blocked on TASK-19 — the next move is the map-model decision, which belongs to a human. Work here resumes when that task is closed.

Two things gate this: the map-model decision, and the lifecycle work it is meant to pin (TASK-1), which is now unblocked and mine to start.

The lifecycle work that was gating this has started, so the map will need pinning to the engine components once the provider seam settles.

The lifecycle provider seam and the Docker provider are in main, so the map can pin the new lifecycle components once the map-model decision lands. Still blocked on TASK-19.

Unblocked: the map-model decision is made and the library is a container (TASK-19). What is left here is the pinning the original task asked for — the new lifecycle seam (provider interface, coordinator, delegated-exec and Docker providers) now has components, and the map should say which files each owns; the duplicate air-cli / cli containers the earlier scan produced are also gone.

Map state at 18d7279 (checked by exporting the map and reading its zones): no unidentified group remains; every component has a container parent, all 20 src files are claimed by a component, and the only zones are the four groups under the library container. The residual is that the library container is a permanent ghost: groma matches a declared container to a scan only when the scanner produces a container fact, and the C# scanner produces those only from application entry points, so a library never gets one and 'groma accept' prints 'no scan match'. The box will render outlined for as long as the library is one container. Pinning work to components (this task) is unaffected.

Status check for the board: this task is not blocked. TASK-19's map-model decision landed and the library is one declared container (sixteen components under it, verified by counting the map tree), so the pinning named here is ordinary work now. It is also unaffected by TASK-27, the follow-on owner decision about that container rendering as an outline rather than a solid box: keeping the outline changes nothing here, and only a decision to split the library per executable would re-open this task's shape.
<!-- SECTION:NOTES:END -->
