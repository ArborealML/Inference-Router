---
id: TASK-14
title: 'Improve the README: patterns from popular .NET repos, plus an icon and banner'
status: Waiting
assignee:
  - '@chatswarm'
created_date: '2026-11-16T22:26:00Z'
updated_date: '2026-11-17T02:26:00Z'
labels:
  - docs
  - ui
dependencies: []
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
- [ ] #5 No claim in the README that SPEC §9.1 does not back with measurements
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Why the image files are still missing: the README pattern review that is recorded here returned art *specifications* only — an aspect-ratio brief for the mark, banner and social preview, and where they should live — and explicitly rendered no artwork. Nothing has been sent to the image service yet, so the absence of `docs/images/` is an undispatched step, not a lost delivery. Next action is an art request (or an in-repo vector mark), then wire the badge/identity into the README.

The mark, banner and social preview are in the repo under docs/images/ as original vector markup, wired into the README with alt text, with provenance and palette in docs/images/README.md. What cannot be verified here: how they look. The markup is well-formed, tiny and free of external references, but rendering (browser and GitHub proxy) and taste are for a human to judge. AC #4 stays open on purpose: the build and licence badges resolve, the release badge cannot exist until a tag is cut, which is a decision on the board.

The visual direction and the repository-settings upload are handed to a human decision task; the remaining README acceptance criteria wait on it.
<!-- SECTION:NOTES:END -->

Status: step 1 landed (findings below); the task moves to In Progress when the README itself changes.
Icon/banner art comes after the structure lands.

## Pattern findings (15 public .NET/C# READMEs, external research request completed)

The report's own stated limits: a purposive high-visibility sample, not a star-ranked top 15; no AIR
binary was executed and no artwork was rendered. Treat each item as a recommendation to check against
this repo, not as a measured fact.

Recurring structure, in the order it recurs:

1. Name, a two-line factual purpose, two or three badges.
2. Status/version and prerequisites, separating what ships now from what is planned.
3. Quick start: install (published binary plus checksum where one exists), configure, run, and one
   request with captured output.
4. A platform/release table (RID matrix plus `SHA256SUMS`) for tools distributed as binaries.
5. Capabilities and limits, naming the compatible subset rather than claiming the whole API.
6. A short neutral configuration sample, with the full schema linked rather than inlined.
7. A CLI/HTTP surface table, with the contract detail left to the spec.
8. A library integration example, only if it compiles exactly as written.
9. Troubleshooting plus security caveats (binding, auth, key handling).
10. Measured performance linked to a dated benchmark note.
11. Minimal build/test/AOT commands, without reproducing CI YAML.
12. Design, roadmap, contributing, license — future work labelled future.

First screen: name, purpose, badges, current scope, one runnable command.

Anti-patterns the survey calls out: marketing adjectives and feature walls; unqualified "OpenAI
compatible"; unverified speed numbers; quick-start commands that no longer work; machine-specific TOML,
keys or model ids; promises about unreleased assets; duplicating the spec, config schema, API contract or
CI config into the README; badge spam and stale tables of contents.

Art guidance: a 1:1 SVG mark that stays legible at 24–32px and displays around 96–160px; an optional
banner at 3:1–4:1 (1200×360–400 source, rendered at ≤960px); GitHub's social preview image separately at
1280×640 under 1 MB. Theme-aware via `<picture>` with a `prefers-color-scheme: dark` source and a
fallback. Keep assets under `docs/images/` (`.github/` stays for workflows and community files), give
every image alt text, and record generated art in `docs/images/ATTRIBUTION.md` with the generator, date,
maintainer edits and reuse terms — without claiming rights beyond what the tool's terms give.
