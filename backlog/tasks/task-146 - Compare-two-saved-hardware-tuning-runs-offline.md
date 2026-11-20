---
id: TASK-146
title: Compare two saved hardware tuning runs offline
status: Done
assignee:
  - '@gpt-6.2-sol-release'
created_date: '2026-11-20T19:07:00Z'
updated_date: '2026-11-20T19:57:00Z'
labels: []
milestone: m-14
dependencies: []
ordinal: 144000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The offline comparison CLI shipped in [PR #54](https://github.com/ArborealML/Inference-Router/pull/54) at 09a0d9b. It compares two saved runs at matched concurrency. All 41 focused Release cases passed. The published Linux x64 Native AOT executable reproduced the reference report byte for byte and rejected a contradictory record with exit 1 and empty stdout. [Post-merge CI](https://github.com/ArborealML/Inference-Router/actions/runs/44805928105) passed all three jobs. Hardware measurement remains separate.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Compare compatible saved runs at the same concurrency with reproducible deltas. Identify unknown or incomparable evidence explicitly.
- [x] #2 Bound input and output and verify file identity on supported systems. Malformed input produces a redacted error with no partial report.
- [x] #3 Exercise the comparison through the CLI with golden and negative controls and verify the published Native AOT binary.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Implement the reviewed v1 two-file comparison on Linux x64 with glibc. Probe required facilities before opening inputs, retain descriptor identity through readback, and recheck link count. Pin original bytes after pathname replacement. Validate bounded JSON and assemble at most 16,384 UTF-8 bytes before output. Check exact CLI output, refusals and redaction with AirTuneCompareOffline and run the golden pair through the published linux-x64 Native AOT CLI. Record the supported scope in SPEC and README.
<!-- SECTION:PLAN:END -->
