---
id: TASK-46
title: Render the final social preview card and verify it from outside the form
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T10:52:00Z'
updated_date: '2026-11-17T10:58:00Z'
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
- [ ] #1 the current card is measured before anything is redrawn and the numbers are recorded on the task: the three text lines are font-size 150, 39 and 27 in docs/images/air-social.svg, so cap heights are about 107 px, 28 px and 19 px in the 1280x640 raster
- [ ] #2 the smallest text line has a cap height of at least 40 px in the 1280x640 raster, measured from the pixels (ink bands per row) rather than read off the SVG; if it does not, the card is re-rendered with larger type or less text until it does
- [ ] #3 legibility is checked at the size a link preview actually shows: downscale to 640x320 and the three lines still separate as three lines with visible counter space, and the mark does not compete with the wordmark
- [ ] #4 docs/images/README.md records the renderer, the font that actually resolved, the byte size and sha256 of the committed PNG, and those numbers are quoted in a comment on TASK-29 so the upload is download-and-pick
- [ ] #5 after a person presses Save, the public check is run and reported: curl -s https://github.com/ArborealML/Inference-Router | grep og:image
<!-- AC:END -->
