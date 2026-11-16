---
id: TASK-12
title: 'External design: CI and tag-release workflow with tested AOT binaries'
status: Done
assignee:
  - '@chatswarm'
created_date: '2026-11-16T20:50:00Z'
updated_date: '2026-11-16T23:22:00Z'
labels:
  - ci
milestone: v0.10 - live view and robustness parity
dependencies: []
priority: high
ordinal: 12000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Design pass handed to the external flagship reviewer; it returns complete commented workflow files plus repo diffs. The exact request is logged in the private research log.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Preflight fails before any build when a release for the tag already exists
- [x] #2 Build and tests run on every push to main and on pull requests
- [ ] #3 Stripped AOT binaries for linux-x64, win-x64, osx-arm64 and osx-x64 built on native runners
- [x] #4 Every binary is smoke-verified headlessly with the inert sample before the release exists
- [x] #5 No CI job can invoke a lifecycle command or a real engine
<!-- AC:END -->

Verified: the design's preflight-before-any-build, native per-RID builds, publish props
(strip/no debug symbols), the per-binary smoke gate, draft-then-publish with SHA256SUMS and draft
cleanup on failure. Deviations recorded: tests run on Linux in CI for now (the suite spawns POSIX
helpers), and there is no lifecycle-exec kill switch in this codebase — the smoke config simply has
no `exec` keys. Its repo findings (relative-URI POSTs, misspelled config env var, `/models` vs
`/v1/models` probe path, no HTTP-visible deadline when every engine is unhealthy) are fixed or filed
on TASK-10.

Verified on Actions: #1 (a run against a tag with a leftover **draft** failed in preflight with
"refusing to build: a draft release for … already exists" and both the build and publish jobs were
skipped), #2 (two green `ci` runs on `main`, one red one fixed), #4 (`tools/ci/smoke.sh` passed inside
the `linux-x64` and `win-x64` legs before any release existed), #5 (the smoke config declares no
lifecycle `exec` keys and CI never talks to a real engine; the only shell commands CI runs are the
in-repo argv round-trip tests). #3 is deliberately left open: the two macOS legs were queued but never
picked up, so osx artifacts are unproven — see TASK-15.
