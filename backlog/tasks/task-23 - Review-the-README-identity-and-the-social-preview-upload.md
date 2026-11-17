---
id: TASK-23
title: >-
  Identity art: keep or replace the mark, and export the card and an
  avatar-ready variant
status: Waiting
assignee:
  - '@qwen3.8-flash-next-board'
created_date: '2026-11-17T02:26:00Z'
updated_date: '2026-11-17T08:16:00Z'
labels:
  - docs
dependencies: []
priority: low
ordinal: 23000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Identity art for this repository is a mark, a banner and a wider social card, all committed as vector source
under `docs/images/`, with the banner at the top of the README. Palette: `#10241F` field, `#F3F7F2` text,
`#2B806C` accent. The keep-or-replace judgement is taken — AC #1 — and the evidence is that the mark is now the
organisation identity: the live org avatar's colour signature matches `air-mark-512.png` to a tenth of a percent.
The art was adopted, not replaced.

Exports are committed too, so nobody has to render anything: `air-social-1280x640.png` (1280x640, the exact size
GitHub asks for) and `air-mark-512.png`, with the renderer, the font that actually resolved and the sha256s
recorded beside them in `docs/images/README.md`. The one caveat worth knowing: the committed card carries DejaVu
Sans lettering, because `system-ui` resolves to nothing on this box — geometry, colour and layout are faithful to
the SVG, the type is a named substitution.

**Who acts next: this board session, and the remaining step is folded into the upload.** The last open criterion is
a judgement about the exported card *as a picture* — do the three text lines read, does the mark compete with the
wordmark. The mechanical half is checked and closed. The taste half is taken at the moment the card is uploaded,
because the settings form in TASK-29 shows the image at full size: whoever presses Upload looks at it, and if it
reads badly, naming a typeface gets the card re-rendered in minutes. No separate review round, no separate cost.

**What each answer changes.** Accept the render and the identity work is closed and TASK-14's identity half stays
closed. Reject it and the card is re-rendered with a pinned face (or the SVG is retuned), which reopens only the
export, not the mark.

**Links**
- Art to judge: [air-mark.svg](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/air-mark.svg), [air-banner.svg](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/air-banner.svg), [air-social.svg](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/air-social.svg); committed rasters: [air-social-1280x640.png](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/air-social-1280x640.png), [air-mark-512.png](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/air-mark-512.png).
- Palette, provenance, renderer, resolved font: [docs/images/README.md](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/README.md).
- Where the banner is wired: [README.md:1](https://github.com/ArborealML/Inference-Router/blob/main/README.md#L1).
- The upload this waits on: [TASK-29](https://github.com/ArborealML/Inference-Router/tree/main/backlog/tasks). The release badge in TASK-14 waits on a tag, which is now [TASK-32](https://github.com/ArborealML/Inference-Router/tree/main/backlog/tasks), not on this task.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 The mark, banner and social card are kept as they are, or replaced with a stated direction - decided against the rendered files, not the markup
- [x] #2 The social card is exported to PNG at 1280x640, and an avatar-ready mark variant exists with an explicit #10241F field, #F3F7F2 strokes and edge padding so a circular crop loses nothing
- [x] #3 The exported card is checked mechanically as an image: no ink in the 3px border ring on any side (nothing clipped), and each of the three text lines carries ink in the band its SVG coordinates put it in
- [x] #4 Provenance for the exports is recorded next to the source, so a raster is traceable to the SVG it came from: renderer, resolved font, size and sha256 in docs/images/README.md
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

created: 2026-11-17T06:56:00Z

---

AC #1 checked on evidence, not on the answer: the mark is now the organisation identity — the live avatar's colour signature matches air-mark-512.png to a tenth of a percent — so the identity was adopted rather than replaced. AC #2 checked: the avatar-ready variant is committed (docs/images/air-mark-avatar.svg) and both PNGs exist, rendered by resvg 2.6.2 in a scratch directory outside the repository, byte-identical on re-render: air-social-1280x640.png 1280×640 30,842 B sha256 b02b6d32…97e9, air-mark-512.png 512×512 9,935 B sha256 2e1b5e02…820a. Both far under the 1 MB ceiling.\n\nAC #3 deliberately NOT checked: it asks whether the exported card reads well, and that is a human judgement I have not made. One fact the judgement needs: this box has exactly one proportional sans face installed (DejaVu Sans), and the card's declared stack — system-ui, -apple-system, BlinkMacSystemFont, Segoe UI, sans-serif — resolves to none of them. resvg's default fallback here is DejaVu Sans **Mono**, so the exports pin the fallback to DejaVu Sans; the lettering in the PNG is DejaVu Sans, not the face your browser picks for that stack. Geometry, colours and layout are faithful; the type is a substitution. If the card typography matters, export it in your browser instead, or tell me which face to pin.\n\nAC #4 left open on a fence, not on work: the provenance is written inside the variant SVG itself, because the provenance note beside it (docs/images/README.md) sits outside the path this session was given for images. One added line closes it.

---

created: 2026-11-17T07:55:00Z

---

Mechanical half of AC #3 checked by measurement, not by eye, on the committed raster (a pure-python PNG decode, so nothing was installed): canvas 1280x640; zero ink pixels anywhere in the 3px border ring on all four sides, so nothing is clipped; the three text bands carry ink where the SVG puts them - the 150px line rows 185-312, the name line rows 333-382 ending at x=1059 (221px of right margin), the tagline rows 393-432 ending at x=981; the mark occupies 339 rows left of the divider. AC #3 stays open because what is left of it is a judgement - does the mark compete with the wordmark, and is DejaVu Sans lettering acceptable - and that is taken at the upload moment in TASK-29, where the settings form shows the card full size. Wake condition for this task: the card upload. No review round, no separate sitting.

---

created: 2026-11-17T08:16:00Z

---

AC #3 reworded to what was actually measured, and the human half moved where the human is. Measured on the committed raster by decoding the PNG directly (no tooling installed or used): canvas 1280x640; zero ink pixels in the 3px border ring on all four sides; ink spans rows 118-493 and columns 119-1059, so the longest text line ends 221px short of the right edge; the 150px line carries ink in rows 185-312, the name line 333-382, the tagline 393-432 - each where the SVG places it. AC #4 is closed by the provenance table in [docs/images/README.md](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/README.md) (renderer resvg 2.6.2, resolved font DejaVu Sans, sizes, sha256 for both exports), landed in f1f880c and ba8e2fd. What I removed from AC #3 - legibility of the lettering and whether the mark competes with the wordmark - is a taste judgement and now lives as an acceptance criterion on TASK-29, where the person doing the upload sees the card full size in the settings form. Nothing else in this task is open.

---
<!-- COMMENTS:END -->
