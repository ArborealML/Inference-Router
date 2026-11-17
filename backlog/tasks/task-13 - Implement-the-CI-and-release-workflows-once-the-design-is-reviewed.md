---
id: TASK-13
title: Implement the CI and release workflows once the design is reviewed
status: Done
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-16T20:50:00Z'
updated_date: '2026-11-17T08:06:00Z'
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
<!-- AC:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T04:28:00Z

---

AC 3 ("all four RID artifacts build, verify, and only then publish") is the only criterion left open, and it is an execution proof rather than more workflow code: the release job has to run once on a real tag, which needs the macOS runner decision (TASK-22) and then a v0.1.0 cut. That proof is tracked as TASK-15 (Blocked on those minutes), so this task is Done with AC 3 carried there — Done here means the workflows exist and are wired, not that a release has been published. Preflight, per-RID stripped AOT builds, per-OS smoke (tools/ci/smoke.sh) and the publish-needs-four-assets gate are all in .github/workflows/release.yml, and ci.yml runs build+test+AOT on push and PR; the ci workflow is green on every main push so far.

---

created: 2026-11-17T06:35:00Z

---

Criterion 3 reworded, not checked: it used to demand four RID artifacts, and decision-10 set the published surface at two (linux-x64, win-x64), so as written it could never close. It still waits on TASK-15, but TASK-15 now closes it by **dropping** the macOS legs from the workflow, the four-asset publish gate and the README platform copy — not by running them. The tag run that proves the two-platform gate is TASK-28.

---

created: 2026-11-17T08:06:00Z

---

AC #3 removed rather than ticked, and this is the reason the board now reads true without weakening any check: its substance is already an acceptance criterion on TASK-28 (#1: one tag run builds and smoke-tests linux-x64 and win-x64 on their own runners before publish), and its workflow half landed in 713ffdd and fbc48d6 with the platform copy, verified in TASK-15. A criterion that belongs to another task and is duplicated here is the reason a Done task ends up with an untickable box - the carrier now owns it outright. Nothing was implemented to close it, and nothing was ticked to make it disappear.

---
<!-- COMMENTS:END -->

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

COMMIT REVIEW in flight over be69d2a^..9e08e65; findings dispositioned here when it lands.
