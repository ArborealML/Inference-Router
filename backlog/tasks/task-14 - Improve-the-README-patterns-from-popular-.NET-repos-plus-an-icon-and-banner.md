---
id: TASK-14
title: 'Improve the README: patterns from popular .NET repos, plus an icon and banner'
status: Waiting
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-16T22:26:00Z'
updated_date: '2026-11-18T19:08:00Z'
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
Wake on the tagged stable release in TASK-28 for the release badge. The landing page groups project identity and badges, provides a runnable fixture smoke test and documents the path from a running engine to a client request.
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

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Refresh the landing page for a developer connecting an inference engine. Group the banner, title and linked badges before prose. Lead with one safe runnable quick start. Consolidate repeated feature, configuration and trust material, shorten release internals to artifact and verification essentials, and link detailed operator and contributor references. Verify commands, sample port, Markdown rendering and local links. Keep the release badge gated on an actual published release.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
README overhaul landed in de3f2ab. The Release CLI built with zero warnings. The fixture smoke passed model discovery, ordinary and streaming chat, CLI chat and the error path. A user-local SDK needed DOTNET_ROOT. Rendered Markdown verified the image group, heading hierarchy, local links and fragment targets. The published canary contains both archives and checksum files. The stable release badge remains pending. Editorial plan and commit reviews were dispatched on 2026-11-18.
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
