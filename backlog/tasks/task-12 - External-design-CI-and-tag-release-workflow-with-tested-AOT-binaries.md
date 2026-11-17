---
id: TASK-12
title: 'External design: CI and tag-release workflow with tested AOT binaries'
status: Done
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-16T20:50:00Z'
updated_date: '2026-11-17T08:06:00Z'
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
- [x] #3 Every binary is smoke-verified headlessly with the inert sample before the release exists
- [x] #4 No CI job can invoke a lifecycle command or a real engine
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Proof: the design landed as the two workflows - [ci.yml](https://github.com/ArborealML/Inference-Router/blob/main/.github/workflows/ci.yml) and [release.yml](https://github.com/ArborealML/Inference-Router/blob/main/.github/workflows/release.yml) - with the preflight refusal exercised in commits 1acdaf8, fc3401a and d3dd153 (draft releases are visible to preflight only with write scope), and the published-surface acceptance criteria now written into [SPEC 14](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md#L1264). Dry-run only: no tag has ever been published, which is TASK-32 and TASK-28.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T05:24:00Z

---

AC #3 ("Stripped AOT binaries for linux-x64, win-x64, osx-arm64 and osx-x64 built on native runners") is removed from this Done task because it was never satisfied and is carried elsewhere, not because it was met. Evidence for leaving it open is on Actions and unchanged since it was recorded: `gh release list` is empty, and the release pipeline still declares osx-arm64 on macos-14 and osx-x64 on macos-13 (.github/workflows/release.yml lines 117-121), the two legs that have only ever queued. The work is carried by **TASK-15** (Blocked on the macOS runner decision in TASK-22), whose acceptance criteria are the same proof: decide the legs, run them green with the smoke test, or stop claiming osx artifacts. The design side of the criterion — that a design exists which specifies all four legs on native runners with stripped AOT publish props — is met and stays described here. Removing the criterion renumbers the old #4 (per-binary smoke) to #3 and the old #5 (no lifecycle exec in CI) to #4; the verification paragraph below now names each gate instead of numbering it. Assignee is the agent who recorded the disposition, not the external service: this task has no live owner-side work left.

---
<!-- COMMENTS:END -->

Verified: the design's preflight-before-any-build, native per-RID builds, publish props
(strip/no debug symbols), the per-binary smoke gate, draft-then-publish with SHA256SUMS and draft
cleanup on failure. Deviations recorded: tests run on Linux in CI for now (the suite spawns POSIX
helpers), and there is no lifecycle-exec kill switch in this codebase — the smoke config simply has
no `exec` keys. Its repo findings (relative-URI POSTs, misspelled config env var, `/models` vs
`/v1/models` probe path, no HTTP-visible deadline when every engine is unhealthy) are fixed or filed
on TASK-10.

Verified on Actions, naming each gate rather than its criterion number: the **preflight-before-any-build**
gate (a run against a tag with a leftover **draft** failed in preflight with "refusing to build: a draft
release for … already exists" and both the build and publish jobs were skipped); the **build and test on
push and pull request** gate (two green `ci` runs on `main`, one red one fixed); the **per-binary smoke
before any release exists** gate (`tools/ci/smoke.sh` passed inside the `linux-x64` and `win-x64` legs);
and the **no lifecycle command in CI** gate (the smoke config declares no lifecycle `exec` keys and CI
never talks to a real engine; the only shell commands CI runs are the in-repo argv round-trip tests).

The **four-RID artifact** gate is the one that did not pass and no longer sits on this task: the two macOS
legs were queued but never picked up, so osx artifacts are unproven. That proof is carried as TASK-15
(decide the legs, run them green with the smoke test, or stop claiming osx artifacts), which is Blocked on
the macOS runner minutes decision in TASK-22.
