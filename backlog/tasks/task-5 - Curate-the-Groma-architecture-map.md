---
id: TASK-5
title: Curate the Groma architecture map
status: Done
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-16T20:40:00Z'
updated_date: '2026-11-16T23:22:00Z'
labels: []
dependencies: []
ordinal: 5000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
First scan produced 18 elements. The C# scanner does not evaluate source generators or ASP.NET references, so treat those scan warnings as scan limits, not repo defects, and place components deliberately.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria

**Owner instruction (on record):** curate this repository's architecture with Groma — run
`groma agent-instructions` and follow its curation workflow, describing the architecture as it exists
today. **Do not change application code** as part of this task.

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 `groma agent-instructions` read and its curation workflow followed (inspect -> structure ->
  describe -> relationships -> flows, guides read for each stage)
- [x] #2 The C# scanner is working again — it is: `groma scanner check` reports "package found · project
  ready · compilation is checked during scan", and `groma scan` refreshes 15 C# elements across 4 project
  declarations. What looked like a broken scanner is the documented scan limit (CSHARP_CONTEXT): external
  packages, MSBuild conditions and source generators are not evaluated, so ASP.NET types (`HttpContext`) and
  generated Tomlyn/STJ types (`TomlSerializer`, `AirServerContext.Default`) surface as CS0246/CS0103/CS0117.
  Same result with and without `dotnet` on PATH, so the container is not the cause — but the map container has
  no .NET toolchain, so **scans run on the host**; the container (air-groma-map, now with git) is view-only.
- [x] #3 The map describes today's architecture: system `inference-router`, containers `air-server` and
  `air-cli`, component groups request-routing (RouterCore, ModelQueue, PrefixAwareScheduler, PrefixTrie),
  identity-and-priority (RouterThread, ThreadPriority), configuration (RouterConfig, RouterConfigLoader,
  ConfigDto), engine-access (EngineRuntime), observability (RouterEvent + EventRing), plus the HTTP surface,
  the source-generated JSON context, both entry points, two flows and three external systems.
  **Recorded deviation:** the bench harness and `tools/ci` are excluded from the map as development tooling
  (Groma's structure guide says exclude it unless the human says otherwise); they are documented in
  `docs/benchmarks.md` and SPEC §14 instead. Say the word and they come back into the map.
- [x] #4 Warning triage: **every** scan warning is a scan limit, not a repo defect — `dotnet build` is
  warnings-as-errors and green on every main commit, and CI AOT-publishes with zero trim warnings, so
  CS0246/CS0103/CS0117/CS0165/CS0534/CS7036 can only come from unresolved packages/generators.
- [x] #5 No application code changed: this task touched `groma/` and the board only.
<!-- AC:END -->

## Curation findings worth a second look

- The scanner derives **no** inter-component relationships (it does not resolve DI or protocol wiring), so all
  20+ rows are authored by hand from call sites. Where a row is an indirect data path rather than a call, the
  row's own wording says so: `PrefixAwareScheduler -> PrefixTrie` ranks by a matched-prefix value the engine
  runtime computes, and `EngineRuntime -> ThreadPriority` is a near-miss (the runtime never inspects priority).
  Current rows cannot be deleted once written, so they were reworded rather than left false.
- `PrefixTrie` is **not** referenced by the scheduler: each `EngineRuntime` owns a trie and the scheduler scores
  `EngineRuntimeInfo.MatchedPrefixChars`. Worth knowing before anyone reads "prefix-aware scheduler" as a shared index.
- The library project produces no container of its own in the scan (only the three executables do), so the
  library's components sit directly under the system. A hand-drafted library container stays a ghost: `groma
  accept` reports "no scan match". Left as-is and documented here.
