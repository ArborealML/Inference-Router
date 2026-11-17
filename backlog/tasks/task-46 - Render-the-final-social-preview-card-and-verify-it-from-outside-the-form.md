---
id: TASK-46
title: Render the final social preview card and verify it from outside the form
status: Waiting
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T10:52:00Z'
updated_date: '2026-11-17T15:56:00Z'
labels:
  - docs
dependencies: []
priority: low
type: docs
ordinal: 45000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Rendering and measurement are done: b214ce6 committed the replacement card (1280x640, 38,150 bytes,
sha256 `cee3237b7f98…98f09`, caps 109/48/42 against a 40 px floor). What is open is AC #5, the
readback from outside the form, which waits for a person to press Save on TASK-29.**

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
- [ ] #5 after a person presses Save, the public check is run and reported: curl -s https://github.com/ArborealML/Inference-Router | grep og:image
<!-- AC:END -->

## Comments

<!-- COMMENTS:BEGIN -->

created: 2026-11-17T13:56:00Z

---

**Render and verification are done and committed: b214ce6.** The coding session replaced `docs/images/air-social.svg` in place and re-rendered; this session re-checked the claim rather than recording it: the committed PNG is 1280x640, 38,150 bytes, sha256 `cee3237b7f980d12259d6aad082a9473f12214115b36e0239d85c8147ec98f09`, and rendering the committed SVG twice reproduces those bytes exactly. Measured with the corrected measurer (the one whose ring check also sees the `#2B806C` accent): text cap heights **109 / 48 / 42 px** against AC #2's 40 px floor — the card it replaces measured 109 / 29 / 20 — ring ink 0 on all four sides at both thresholds, rightmost ink x=1200 (a 79 px margin), 3/3 bands still carrying ink at 640x320, light 4.8%. The provenance row and the cap-floor note are in [docs/images/README.md](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/README.md), including the honest limit that the raster's lettering is DejaVu Sans at a cap ratio of about 0.73 em, so the 40 px floor is proven for this machine's face and not for one a browser would pick.

AC #1, #2, #3 and #4 are checked on that evidence; **#5 stays open**, which is why the task is Waiting and not Done. **Wake: the owner pressing Save on TASK-29** — #5 is the public readback that only a live setting can answer. If the owner takes TASK-49 item (d) and asks for a different composition, this reopens for one render and one re-measure.

---

<!-- COMMENTS:END -->
