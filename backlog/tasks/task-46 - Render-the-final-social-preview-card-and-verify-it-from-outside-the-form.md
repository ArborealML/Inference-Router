---
id: TASK-46
title: Render the final social preview card and verify it from outside the form
status: Done
assignee:
  - '@claire'
created_date: '2026-11-17T10:52:00Z'
updated_date: '2026-11-18T19:29:00Z'
labels:
  - docs
milestone: m-9
dependencies: []
priority: low
type: docs
ordinal: 45000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Claire's act, roughly two minutes:** open the repository's Settings → Social preview, paste the
committed `docs/images/air-social-1280x640.png`, press Save, then run
`curl -s https://github.com/ArborealML/Inference-Router | grep og:image` and paste the line back.
Everything else here is done: b214ce6 committed the card at the measured sizes (1280x640, 38,150
bytes, sha256 `cee3237b7f98…98f09`, caps 109/48/42 against a 40 px floor), and only AC #5's readback
from outside the form is open.

**The finding first: the type is small, and it is measured, not a matter of taste.** The committed
[docs/images/air-social-1280x640.png](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/air-social-1280x640.png)
was 1280x640, 30,842 bytes, sha256 `b02b6d32...97e9` until b214ce6 replaced it, rendered by resvg 2.6.2 with **DejaVu Sans** resolved —
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
- [x] #1 The card was measured before redrawing and the numbers recorded: the three text lines are `font-size` 150, 39 and 27 in `docs/images/air-social.svg`, so caps are about 107/28/19 px
- [x] #2 The smallest text line's measured cap height is at least 40 px in the 1280x640 raster, from pixel ink bands not the SVG; otherwise the card is redrawn with larger type or less text
- [x] #3 At a real 640x320 downscale every text line still separates as its own ink band, and ink ends at least 60 px from the right edge
- [x] #4 docs/images/README.md records the renderer, the font that actually resolved, and the byte size and sha256 of the committed PNG, quoted where the upload is tracked so the upload is checkable
- [x] #5 The public readback is run and reported: the repository page og:image resolves to a byte-identical copy of the committed card
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**AC #5 ran, and the premise it was written on was wrong.** GitHub applies a social-preview upload as soon as the file is accepted: there is no Save control in that pane, and re-uploading is not what makes it take effect. Measured at 03:16Z: the repository page's og:image resolves to a repository-images address; fetched and hashed here it is 38,150 bytes, 1280x640, sha256 `cee3237b7f98…98f09` — byte-identical to `docs/images/air-social-1280x640.png` as committed at b214ce6. The card was already live; what the criterion asked for was a step that does not exist, so it now names the readback instead.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
<!-- COMMENTS:END -->
