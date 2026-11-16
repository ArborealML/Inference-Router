---
id: TASK-14
title: 'Improve the README: patterns from popular .NET repos, plus an icon and banner'
status: To Do
assignee:
  - '@chatswarm'
created_date: '2026-11-16T22:26:00Z'
updated_date: '2026-11-16T23:22:00Z'
labels:
  - docs
  - ui
dependencies: []
priority: low
ordinal: 14000
---

## Description

Low priority. The README is accurate but plain: no identity (icon/banner), no badges, and it leads
with prose instead of what the reader can do in 60 seconds. Two steps:

1. **Pattern research** (dispatched, logged below): review the READMEs of widely used .NET OSS repos
   and extract the structure, badge, section-order and example conventions that recur, plus what to
   avoid.
2. **Apply + art**: restructure our README around those findings and add an icon and a banner image
   (generated on demand through the external image service once the shape is settled).

Keep whatever the research recommends that does not fight the house style: no marketing voice, no
feature-wall, no claim that is not measured (SPEC §9.1 gate).

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Pattern findings from popular .NET repos recorded in this task (or a short note under docs/)
- [ ] #2 README restructured with a recognised section order: identity, what it does, quick start,
  configuration, CI/release, status/scope, layout, license
- [ ] #3 An icon and a banner image committed under `docs/images/` (or `.github/`), with alt text and
  a source/attribution note; no third-party assets with unclear licences
- [ ] #4 Build status and release badges that actually resolve on the public repo
- [ ] #5 No claim in the README that SPEC §9.1 does not back with measurements
<!-- AC:END -->

Status: not started — pattern research is in flight (deep tier, logged in `.local/research-notes.md`); the task moves to In Progress when the README itself changes. Icon/banner art comes after the structure lands.
