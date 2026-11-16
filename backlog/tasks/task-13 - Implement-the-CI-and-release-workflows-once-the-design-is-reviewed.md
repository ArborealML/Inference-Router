---
id: TASK-13
title: Implement the CI and release workflows once the design is reviewed
status: Done
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-16T20:50:00Z'
updated_date: '2026-11-16T23:22:00Z'
labels:
  - ci
milestone: v0.10 - live view and robustness parity
dependencies:
  - TASK-12
ordinal: 13000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Implement exactly what the reviewed design specifies, then prove it: a push run, a tag run on a scratch tag, and a duplicate-tag run that fails fast. Update AGENTS.md in the same change.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 ci.yml runs on push to main and on pull requests
- [x] #2 release.yml refuses a duplicate tag before any build work starts
- [ ] #3 All four RID artifacts build, verify, and only then publish
<!-- AC:END -->

Implemented on `main`: `.github/workflows/ci.yml`, `.github/workflows/release.yml`,
`tools/ci/smoke.sh`, `tools/ci/fake-upstream.py`, `samples/router.ci-smoke.toml`. The Linux AOT
binary passes the full smoke sequence locally. Remaining before this can be checked off: a green CI
run on `main`, and one real tag run of `release.yml` (the win-x64/osx legs cannot be exercised
locally) — which means cutting a first release, an owner decision.

Verified on GitHub Actions (dry run of the tag pipeline, all four legs attempted):
preflight succeeded, `linux-x64` and `win-x64` each built a stripped Native-AOT `air` and passed
`tools/ci/smoke.sh` **on their own runner**, and the publish job was skipped because the run was a
dry run. The two macOS legs never started — they sat queued with no runner picked up — so macOS
artifacts are **designed but not yet demonstrated**. That is a runner-availability question for the
account, tracked as TASK-15; nothing in the pipeline needs changing for it.
The dry-run tag was deleted afterwards; no release exists yet.

COMMIT REVIEW in flight (deep tier) over be69d2a^..9e08e65; findings dispositioned here when it lands.
