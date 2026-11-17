---
id: TASK-14
title: 'Improve the README: patterns from popular .NET repos, plus an icon and banner'
status: Waiting
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-16T22:26:00Z'
updated_date: '2026-11-17T16:18:00Z'
labels:
  - docs
  - ui
dependencies:
  - TASK-28
priority: low
ordinal: 14000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Low priority. The README is accurate but plain: no identity (icon/banner), no badges, and it leads
with prose instead of what the reader can do in 60 seconds. Two steps:

1. **Pattern research** (dispatched, logged below): review the READMEs of widely used .NET OSS repos
   and extract the structure, badge, section-order and example conventions that recur, plus what to
   avoid.
2. **Apply + art**: restructure our README around those findings and add an icon and a banner image
   (generated on demand through the external image service once the shape is settled).

Keep whatever the research recommends that does not fight the house style: no marketing voice, no
feature-wall, no claim that is not measured (SPEC §9.1 gate).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Pattern findings from popular .NET repos recorded in this task (or a short note under docs/)
- [x] #2 README restructured with a recognised section order: identity, what it does, quick start,
  configuration, CI/release, status/scope, layout, license
- [x] #3 An icon and a banner image committed under `docs/images/` (or `.github/`), with alt text and
  a source/attribution note; no third-party assets with unclear licences
- [ ] #4 Build status and release badges that actually resolve on the public repo
- [x] #5 No claim in the README that SPEC §9.1 does not back with measurements
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**What is left, and why it is not finished.** The mark, banner and social preview are in the repository as vector markup, wired into the README, with
provenance and palette in `docs/images/README.md`. How they look — rendering in a browser and through GitHub's proxy,
and taste — is for a person, and that half lives on the upload task.

**The one open criterion is about a tag, not the README.** The build and licence badges resolve; a release badge cannot
exist until a tag exists. **Wake, as an event:** the first tag run on [TASK-28](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-28%20-%20Cut-the-first-release-tag-and-watch-the-release-run.md) publishing real assets.

Earlier rounds of this task — the pattern review that returned art specifications only, the identity review, the
argument over whether the mark was undispatched art — are superseded history: [board trim, 2026-11-17](https://github.com/ArborealML/Inference-Router/blob/main/backlog/archive/board-trim-2026-11-17.md).
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->

created: 2026-11-17T13:56:00Z

---

**Wake re-named, because its old wake is closed.** This task waited on TASK-23, which is now Done. Its only open criterion is the release badge, which cannot exist before the first release tag: wake is now the tag in TASK-28 landing and the workflow badge resolving on `main`. The README prose and identity half are unaffected.

---

<!-- COMMENTS:END -->

Status: step 1 landed (findings below); the task moves to In Progress when the README itself changes.
Icon/banner art comes after the structure lands.
