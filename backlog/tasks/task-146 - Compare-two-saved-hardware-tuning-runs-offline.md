---
id: TASK-146
title: Compare two saved hardware tuning runs offline
status: In Progress
assignee:
  - '@gpt-6.2-sol-config'
created_date: '2026-11-20T19:07:00Z'
updated_date: '2026-11-20T19:12:00Z'
labels: []
milestone: m-14
dependencies: []
ordinal: 144000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Config owns the approved offline CLI for comparing two saved hardware tuning runs against the [landed design](../../docs/hardware-tuning-design.md). Compare measured capacity and sustained decode at matched concurrency. Represent unknown or incomparable evidence explicitly.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Compare compatible saved runs at the same concurrency with reproducible deltas. Identify unknown or incomparable evidence explicitly.
- [ ] #2 Bound input and output and verify file identity on supported systems. Malformed input produces a redacted error with no partial report.
- [ ] #3 Exercise the comparison through the CLI with golden and negative controls and verify the published Native AOT binary.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Implement the reviewed v1 two-file comparison on Linux x64 with glibc. Probe required facilities before opening inputs, retain descriptor identity through readback, and recheck link count. Pin original bytes after pathname replacement. Validate bounded JSON and assemble at most 16,384 UTF-8 bytes before output. Check exact CLI output, refusals and redaction with AirTuneCompareOffline and run the golden pair through the published linux-x64 Native AOT CLI. Record the supported scope in SPEC and README.
<!-- SECTION:PLAN:END -->
