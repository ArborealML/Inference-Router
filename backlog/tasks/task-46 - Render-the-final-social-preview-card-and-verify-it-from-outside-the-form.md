---
id: TASK-46
title: Render the final social preview card and verify it from outside the form
status: Waiting
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T10:52:00Z'
updated_date: '2026-11-17T14:02:00Z'
labels:
  - docs
dependencies: []
priority: low
type: docs
ordinal: 45000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Do this (about 30 minutes, no browser, no GitHub settings).** The card half of the
social-preview work, so that the only thing left for a person is picking a file in a form.

**The finding first: the type is small, and it is measured, not a matter of taste.** The committed
[docs/images/air-social-1280x640.png](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/air-social-1280x640.png)
is 1280x640, 30,842 bytes, sha256 `b02b6d32...97e9`, rendered by resvg 2.6.2 with **DejaVu Sans** resolved —
the only proportional sans on this box, already documented in
[docs/images/README.md](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/README.md), so the
font is not the problem. The three text lines in
[docs/images/air-social.svg](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/air-social.svg)
are `font-size` 150, 39 and 27, giving cap heights near 107, 28 and 19 px. A GitHub link preview shows the card
at roughly 600 px wide, so the tagline's caps land near 9 px. Fix it by raising the type or cutting a line,
then re-render and measure the pixels (ink per row), and check the 640x320 downscale.

    node /tmp/svg-render/export.js   # the command README documents; byte-identical on re-render
    sha256sum docs/images/air-social-1280x640.png

Then put the byte size and sha256 in a comment on [TASK-29](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-29%20-%20Upload-the-social-preview-card-and-the-repository-avatar.md) so
the upload is download-and-pick.

**Do not upload the bare mark:** [docs/images/air-mark.svg](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/air-mark.svg)
strokes in `currentColor` on no field, so standalone it is black on transparent and invisible on a dark frame.

**Not in scope:** the upload itself — GitHub has no API for the social-preview image, which is why
[TASK-29](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-29%20-%20Upload-the-social-preview-card-and-the-repository-avatar.md) stays a person's
two minutes. The avatar is live and already verified from outside the form; see [TASK-23](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-23%20-%20Review-the-README-identity-and-the-social-preview-upload.md).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 the current card is measured before anything is redrawn and the numbers are recorded on the task: the three text lines are font-size 150, 39 and 27 in docs/images/air-social.svg, so cap heights are about 107 px, 28 px and 19 px in the 1280x640 raster
- [x] #2 the smallest text line has a cap height of at least 40 px in the 1280x640 raster, measured from the pixels (ink bands per row) rather than read off the SVG; if it does not, the card is re-rendered with larger type or less text until it does
- [x] #3 legibility is checked at the size a link preview actually shows, on a real 640x320 downscale rather than an estimate: every text line still separates as its own ink band, and the card keeps at least a 60 px right-edge ink margin. Whether the mark competes with the wordmark is a taste call, taken at the upload moment, and lives as TASK-29 AC #4
- [x] #4 docs/images/README.md records the renderer, the font that actually resolved, the byte size and sha256 of the committed PNG, and those numbers are quoted in a comment on TASK-29 so the upload is download-and-pick
- [ ] #5 after a person presses Save, the public check is run and reported: curl -s https://github.com/ArborealML/Inference-Router | grep og:image
<!-- AC:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T13:43:00Z

---

**The image-prep half ran as a subagent in a scratch directory (no repo writes); it is done, and this board session re-measured its numbers rather than repeating them.** Artifacts are kept untracked under `.local/card-prep/`: `air-social-candidate-A.svg` + `-1280x640.png` (recommended), candidate B (tagline copy cut) and candidate C (full re-composition) with their rasters, a 640x320 preview raster of A, the measurement and downscale scripts, and the child's full report (`CARD-REPORT.md`).

**Verified by this session, not inherited.** Rendering `.local/card-prep/air-social-candidate-A.svg` twice with the documented pipeline (resvg 2.6.2 via `@resvg/resvg-js`, `defaultFontFamily`/`sansSerifFamily` pinned to DejaVu Sans, `fitTo:{mode:'original'}`) gives the same bytes both times and equals the shipped PNG: 1280x640, 38,150 bytes, sha256 `cee3237b7f980d12259d6aad082a9473f12214115b36e0239d85c8147ec98f09`. Measured from pixels with the corrected measurer: text bands at rows 191-299, 332-380, 408-460 with cap heights **109 / 48 / 42 px** against AC #2's 40 px floor; ring ink 0 on all four sides at both the light threshold and the off-field threshold that also sees the `#2B806C` accent; rightmost ink at x=1200, so a 79 px right margin; 3/3 bands still carry ink at 640x320. The committed card, measured the same way in the same text column, gives caps **109 / 29 / 20 px** — the 20 px tagline is the defect AC #2 exists to kill, and A takes it to 42 px.

**What the coding session owes before committing any of this** (a provenance claim is only as good as the check someone ran): (1) re-render inside the repo from the committed SVG and require the raster to match the artifact's sha256 byte-for-byte, so the PNG in `docs/images/` is provably the render of the SVG next to it; (2) re-measure dimensions and cap heights after that render rather than trusting this comment; (3) update the `docs/images/README.md` row with the renderer, the font that actually resolved, the size, the bytes and the sha256, and keep the note that the raster's lettering is **DejaVu Sans**, not the `system-ui` stack the SVG asks for — the child's honest limit, worth repeating: DejaVu Sans measures a cap ratio of about 0.73 em, and a face with a smaller cap ratio would pull A's font-size-56 tagline back under the 40 px floor, so the floor is proven for this box's face only.

**The remaining choice is copy, not layout, and it is cheap to change now and expensive to change after upload.** A keeps all three lines verbatim and raises the tagline to font-size 56 — which puts it close to the 64 px name line, so the hierarchy rests on colour. B deletes the tagline copy entirely and gets bigger type (min cap 47) and a smaller file (29,005 B). C re-composes the card (mark centred on top, horizontal accent rule) with min cap 44 and the widest safety margins but the smallest mark. Which one ships is a taste call; A is the recommendation because it changes the least and loses no copy.

---

created: 2026-11-17T13:56:00Z

---

**Render and verification are done and committed: b214ce6.** The coding session replaced `docs/images/air-social.svg` in place and re-rendered; this session re-checked the claim rather than recording it: the committed PNG is 1280x640, 38,150 bytes, sha256 `cee3237b7f980d12259d6aad082a9473f12214115b36e0239d85c8147ec98f09`, and rendering the committed SVG twice reproduces those bytes exactly. Measured with the corrected measurer (the one whose ring check also sees the `#2B806C` accent): text cap heights **109 / 48 / 42 px** against AC #2's 40 px floor — the card it replaces measured 109 / 29 / 20 — ring ink 0 on all four sides at both thresholds, rightmost ink x=1200 (a 79 px margin), 3/3 bands still carrying ink at 640x320, light 4.8%. The provenance row and the cap-floor note are in [docs/images/README.md](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/README.md), including the honest limit that the raster's lettering is DejaVu Sans at a cap ratio of about 0.73 em, so the 40 px floor is proven for this machine's face and not for one a browser would pick.

AC #1, #2, #3 and #4 are checked on that evidence; **#5 stays open**, which is why the task is Waiting and not Done. **Wake: the owner pressing Save on TASK-29** — #5 is the public readback that only a live setting can answer. If the owner takes TASK-49 item (d) and asks for a different composition, this reopens for one render and one re-measure.

---
<!-- COMMENTS:END -->
