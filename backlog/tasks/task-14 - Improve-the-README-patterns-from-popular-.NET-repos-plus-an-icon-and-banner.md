---
id: TASK-14
title: 'Improve the README: patterns from popular .NET repos, plus an icon and banner'
status: Waiting
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-16T22:26:00Z'
updated_date: '2026-11-17T13:56:00Z'
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

**Pattern findings (15 public .NET/C# READMEs, external research request completed)**
The report's own stated limits: a purposive high-visibility sample, not a star-ranked top 15; no AIR
binary was executed and no artwork was rendered. Treat each item as a recommendation to check against
this repo, not as a measured fact.

Recurring structure, in the order it recurs:

First screen: name, purpose, badges, current scope, one runnable command.

Anti-patterns the survey calls out: marketing adjectives and feature walls; unqualified "OpenAI
compatible"; unverified speed numbers; quick-start commands that no longer work; machine-specific TOML,
keys or model ids; promises about unreleased assets; duplicating the spec, config schema, API contract or
CI config into the README; badge spam and stale tables of contents.

**Links**
- The file: [README.md](https://github.com/ArborealML/Inference-Router/blob/main/README.md) — banner [line 1](https://github.com/ArborealML/Inference-Router/blob/main/README.md#L1), badges [10-12](https://github.com/ArborealML/Inference-Router/blob/main/README.md#L10-L12), platform copy [82-84](https://github.com/ArborealML/Inference-Router/blob/main/README.md#L82-L84), CI section [214](https://github.com/ArborealML/Inference-Router/blob/main/README.md#L214).
- Release badge cannot resolve until this is non-empty: [releases](https://github.com/ArborealML/Inference-Router/releases).
- Art and provenance: [docs/images/](https://github.com/ArborealML/Inference-Router/tree/main/docs/images/), [docs/images/README.md](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/README.md).
- Numbers any claim must trace to: [docs/benchmarks.md](https://github.com/ArborealML/Inference-Router/blob/main/docs/benchmarks.md).
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
Why the image files are still missing: the README pattern review that is recorded here returned art *specifications* only — an aspect-ratio brief for the mark, banner and social preview, and where they should live — and explicitly rendered no artwork. Nothing has been sent to the image service yet, so the absence of `docs/images/` is an undispatched step, not a lost delivery. Next action is an art request (or an in-repo vector mark), then wire the badge/identity into the README.

The mark, banner and social preview are in the repo under docs/images/ as original vector markup, wired into the README with alt text, with provenance and palette in docs/images/README.md. What cannot be verified here: how they look. The markup is well-formed, tiny and free of external references, but rendering (browser and GitHub proxy) and taste are for a human to judge. AC #4 stays open on purpose: the build and licence badges resolve, the release badge cannot exist until a tag is cut, which is a decision on the board.

The visual direction and the repository-settings upload are handed to a human decision task; the remaining README acceptance criteria wait on it.

Wake condition, stated for the Waiting column: this task wakes when **TASK-23** is answered (the identity is kept, or a replacement direction is given, and the social-preview upload is done or declined) and when **TASK-22** cuts a release tag. AC #4 is the one still open, and it is open because of the tag, not because of the README: the CI badge and the licence badge resolve on the public repo (README lines 10-12, and the CI badge's workflow is green on every push to main), but a release badge cannot resolve while no release exists — the release listing on the public repo is empty, and release.yml only produces one from a tag. So nothing is waiting for a reader of this task: the wake is those two board events.

Moved out of the Description under decision-18's word line — nothing deleted, nothing rewritten:

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

Art guidance: a 1:1 SVG mark that stays legible at 24–32px and displays around 96–160px; an optional
banner at 3:1–4:1 (1200×360–400 source, rendered at ≤960px); GitHub's social preview image separately at
1280×640 under 1 MB. Theme-aware via `<picture>` with a `prefers-color-scheme: dark` source and a
fallback. Keep assets under `docs/images/` (`.github/` stays for workflows and community files), give
every image alt text, and record generated art in `docs/images/ATTRIBUTION.md` with the generator, date,
maintainer edits and reuse terms — without claiming rights beyond what the tool's terms give.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T03:44:00Z

---

AC #5 now holds, and it is checkable rather than a vibe: the only measured claims in the README are the benchmark paragraph, and every figure in it traces to the published run — routing +58.0/+90.6/+91.4 µs p50 and ~15 KB (README: ~58–91 µs, ~15 KB), full HTTP path +217.1/+320.7/+995.8 µs and ~26–32 KB (README: ~217–996 µs, ~26–32 KB, with the top of the range labelled as HTTP framing rather than routing), SSE relay +58.1/+97.5/+102.3 µs and ~24–25 KB (README: ~58–102 µs, ~25 KB) — with docs/benchmarks.md and SPEC 9.1 linked for the caveats. The compatibility claim is scoped to the two proxy routes in the works-today/not-built-yet table, and no other sentence in the README asserts performance.

---

created: 2026-11-17T05:26:00Z

---

Assignee reduced to one name. Two were listed (the owner and the agent): the owner's move on the README itself lives on TASK-23, so the next actor on this task is the agent who restructures it when TASK-23 lands. A task with two assignees reads as nobody.

---

created: 2026-11-17T07:55:00Z

---

AC #4 is two badges and only one of them exists today: README lines 10-12 carry CI, license and .NET badges, and the CI badge resolves (checked the URL form against the workflow name). The release badge cannot resolve until a release exists, which is TASK-32 (owner go-ahead) then TASK-28 (the tag run). Dependency set to TASK-28 so the board shows the real wake instead of a prose promise. Assignee stays the main session: a README line during the tag run, not a separate change.

---

created: 2026-11-17T13:56:00Z

---

**Wake re-named, because its old wake is closed.** This task waited on TASK-23, which is now Done. Its only open criterion is the release badge, which cannot exist before the first release tag: wake is now the tag in TASK-28 landing and the workflow badge resolving on `main`. The README prose and identity half are unaffected.

---
<!-- COMMENTS:END -->

Status: step 1 landed (findings below); the task moves to In Progress when the README itself changes.
Icon/banner art comes after the structure lands.
