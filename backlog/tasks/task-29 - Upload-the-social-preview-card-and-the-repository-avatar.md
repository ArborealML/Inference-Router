---
id: TASK-29
title: Upload the social preview card and the repository avatar
status: Blocked
assignee:
  - '@claire'
created_date: '2026-11-17T05:59:00Z'
updated_date: '2026-11-17T17:42:00Z'
labels:
  - docs
dependencies:
  - TASK-23
  - TASK-46
priority: low
ordinal: 23500
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Two minutes in a browser, and the file is already on this machine.** Answer one line here: `card set`,
`card declined: REASON`, or `cannot set it: REASON`.

1. Go to the repository's **Settings**, **General** tab, scroll to **Social preview images**:
   [github.com/ArborealML/Inference-Router/settings](https://github.com/ArborealML/Inference-Router/settings).
2. Choose an image and pick **`$HOME/Downloads/air-social-1280x640.png`** — an agent put that exact copy there at
   17:2xZ and byte-checked it against the committed blob: 1280x640 PNG, 38,150 bytes, sha256
   `cee3237b7f98…98f09`. If it is gone, download
   [docs/images/air-social-1280x640.png](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/air-social-1280x640.png)
   ([raw bytes](https://github.com/ArborealML/Inference-Router/raw/main/docs/images/air-social-1280x640.png))
   and `sha256sum` it before picking it: the hash must match.
3. Press **Save**. The box shows a thumbnail whether Save worked or not — that is how three earlier `card set`
   answers produced no change.
4. Paste this into the task so the check is on the record:

       curl -s https://github.com/ArborealML/Inference-Router | grep -o '<meta property="og:image"[^>]*>'

   Ours prints `opengraph.githubassets.com/...` once the card is live; GitHub's generated card prints
   `repository-images.githubusercontent.com/1460450305/...`. Re-checked at 19:0xZ: it prints the second, so the
   upload still has not taken.

**Nothing to do for the avatar:** the organisation picture is live and verified from outside the form (TASK-23).
GitHub exposes no API and no `gh` endpoint for the social-preview image, which is the only reason this task is a
person's; the file, its measurements and the after-the-fact public check are agent work.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The social-preview image is set, or declined with a one-line reason, and the answer line is in this task
- [x] #2 The organisation picture is set from the avatar-ready mark (TASK-23), or declined with a one-line reason
- [ ] #3 The `og:image` meta tag on the public repository page prints `opengraph.githubassets.com`, quoted here
- [ ] #4 The picked file's sha256 is recorded here and equals the committed card's `cee3237b7f98…98f09`
- [ ] #5 At the upload moment the three text lines read at full size and the mark does not compete with the wordmark; if it reads badly, name the typeface and the card goes back for one re-render
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**What changes when it lands.** Every link preview of the repository — issues, PRs, a README shared outside GitHub —
renders our card instead of GitHub's mostly-white generated one. Replacing the image later is a second trip to the same
form, so nothing here is permanent and nothing is burned.

**Links**
- The settings page and the box: [repository Settings](https://github.com/ArborealML/Inference-Router/settings) -> **General** -> **Social preview images**.
- The file to pick, committed at b214ce6: [docs/images/air-social-1280x640.png](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/air-social-1280x640.png); [raw bytes](https://github.com/ArborealML/Inference-Router/raw/main/docs/images/air-social-1280x640.png); provenance, renderer and the resolved font in [docs/images/README.md](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/README.md).
- The card's measurements, from pixels not the SVG: text cap heights 109 / 48 / 42 px against a 40 px floor, right ink margin 79 px, and 3/3 text bands still separating at a 640x320 downscale. The card it replaced measured 109 / 29 / 20 — the 20 px tagline is why it was re-rendered.
- Vector source, if a different typeface is wanted: [air-social.svg](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/air-social.svg). The raster's lettering is DejaVu Sans, not the browser face the SVG asks for.
- **Do not pick the bare mark:** [air-mark.svg](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/air-mark.svg) strokes in `currentColor` on no field, so it is black on transparent and invisible on a dark frame. The standalone form is [air-mark-512.png](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/air-mark-512.png), already used for the [organisation picture](https://github.com/organizations/ArborealML/settings/profile).
- Readback from outside the form: [the link-preview object](https://opengraph.githubassets.com/1/ArborealML/Inference-Router) and the `og:image` tag quoted above.

Superseded log entries for this task: [board trim, 2026-11-17](https://github.com/ArborealML/Inference-Router/blob/main/backlog/archive/board-trim-2026-11-17.md).
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
<!-- COMMENTS:END -->
