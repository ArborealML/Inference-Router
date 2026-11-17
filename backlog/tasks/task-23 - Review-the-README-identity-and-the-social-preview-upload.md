---
id: TASK-23
title: >-
  Identity art: keep or replace the mark, and export the card and an
  avatar-ready variant
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T02:26:00Z'
updated_date: '2026-11-17T06:06:00Z'
labels:
  - docs
dependencies: []
priority: low
ordinal: 23000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
A mark, a banner and a wider social card were generated as vector art and committed under docs/images/, with the banner at the top of the README. Look at them and say keep or replace. Two things only you can do: the social preview has to be exported to PNG or JPEG and pasted into the repository settings on GitHub, and if the mark should also be the organisation or repository avatar, that is your upload. The palette is dark evergreen (#10241F field, #2B806C accent); if you want a different colour, it is a three-file change and no code is involved.

What each answer changes: **keep** leaves the committed art as it is and leaves only the settings upload; **replace with a direction** sends the direction to the generator and the three files are recommitted — palette changes included, no code touched. What it unblocks: TASK-14, the README work — its identity half is closed the moment you say keep, and a replacement direction reopens it. Its last open acceptance criterion (a release badge that resolves) does not wait on this task: it waits on a release tag, which is TASK-22.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The mark, banner and social card are kept as they are, or replaced with a stated direction - decided against the rendered files, not the markup
- [ ] #2 The social card is exported to PNG at 1280x640, and an avatar-ready mark variant exists with an explicit #10241F field, #F3F7F2 strokes and edge padding so a circular crop loses nothing
- [ ] #3 The exported card is checked as an image: all three text lines legible at 1280x640, nothing clipped, the mark not competing with the wordmark
- [ ] #4 Provenance for the exports is recorded next to the source, so a raster is traceable to the SVG it came from
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Board audit: the steps, the cost, the upload that only a human can do and the answer format were already here. Added the consequences of each option and what this task unblocks, which the working contract requires on a human task.

Upload half moved to TASK-29: the social-preview image and the organisation profile picture are authenticated web uploads with no API, so they stay human. Everything up to the upload stays here - the keep-or-replace call, the exports, and the avatar-ready variant. Two facts found while writing TASK-29, both of which block a naive export: (1) GitHub has no repository image other than the social preview, so the mark can only be the org avatar; (2) air-mark.svg strokes in currentColor and declares no field, so a standalone render is black-on-transparent, invisible on a dark frame, and needs an explicit field and stroke colour before anything uploads it. Third constraint, real and not cosmetic: this development box has no SVG rasteriser, no headless browser and no Python image library, and the card sets its text in a system-ui stack - so the export has to run where a browser or converter lives, and a hand-drawn re-render is not a substitute for it.

Fence note: this task was moved to the agent queue, but its output lands under the images directory in the repository, and the code session currently holds that path by instruction. So either the code session commits the two exported files, or the fence comes off the images directory for this one edit. Flagged rather than assumed.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T05:55:00Z

---

Owner instruction, so reassigned — with the one human-only step split out rather than silently dropped. Judging the mark and banner, choosing a palette direction, and exporting the social card to PNG/JPEG are all doable here (the SVGs are in-repo vector source). Setting GitHubs social-preview image and the repository avatar is not: it is an authenticated web-settings upload with no API, so it now lives as TASK-29, one minute, one upload. AC #2 stays about that upload.

---
<!-- COMMENTS:END -->

## Do this (about 3 minutes)

1. Open `docs/images/air-banner.svg` and `docs/images/air-mark.svg` in a browser, or just look at
   <https://github.com/ArborealML/Inference-Router> — the banner is at the top of the README.
2. Say `keep` or `replace with <direction>`. The palette is in `docs/images/README.md`; a colour change
   is a three-file edit and I can do it in minutes.
3. One thing only a human can do: GitHub's social card is an upload, not a committed file. Repo
   **Settings → Options → Social preview**, upload a PNG or JPEG exported from `docs/images/air-social.svg`
   (1280×640). I cannot export or set it.

Answer format: `TASK-23: keep, card set` or `TASK-23: replace: <direction>`.
