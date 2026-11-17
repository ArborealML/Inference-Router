---
id: TASK-29
title: Upload the social preview card and the organisation avatar
status: Blocked
assignee:
  - '@claire'
created_date: '2026-11-17T05:59:00Z'
updated_date: '2026-11-17T06:56:00Z'
labels:
  - docs
dependencies:
  - TASK-23
priority: low
ordinal: 23000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->

Two uploads on GitHub that no agent can do, and one correction to the task that used to carry them: **GitHub has
no repository image except the social preview**, so the second upload is the **organisation** profile picture,
not a repository avatar. Everything up to the upload is repository work and is handled elsewhere — the vector
source is committed, and TASK-23 produces the avatar-ready variant of the mark (see the note below on why the
mark needs one before it can be uploaded anywhere).

**Set the social card** → links to the repository shared on any site render our card: dark evergreen field, the
branching-route mark, `AIR`, the name and the tagline. **Decline it** → GitHub renders its own generated card
from the repository description, which is a grey box that says nothing about what this is, and nothing in the
repository changes. **Set the org avatar** → the mark appears on the org page, on the repository owner chip
beside every repository, and in the search results. **Decline it** → the org keeps a letter avatar. Neither
upload is load-bearing for the product, and both are one-time.

**Why this task cannot be done by an agent:** both settings are behind an authenticated web form. There is no
API, no `gh` endpoint, and no committed file that stands in for the upload. A session can prepare the exact PNG
and verify the result afterwards; it cannot press the button.

**Why this task cannot be done by an agent *and* left ready in the repo either:** the card is not a pure-geometry
render. `air-social.svg` sets its text in a `system-ui` stack, so a faithful rasterisation needs a browser's font
stack, and the box this repository is developed on has no SVG rasteriser, no headless browser and no Python image
library installed. The one-command export below is deliberately something to run where a browser or a converter
already lives.

**Links**
- Card source, opens at native 1280×640: [air-social.svg](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/air-social.svg) — for browser capture use the raw form: [raw.githubusercontent.com/…/air-social.svg](https://raw.githubusercontent.com/ArborealML/Inference-Router/main/docs/images/air-social.svg).
- Mark, do NOT upload raw (currentColor with no field renders black-on-transparent): [air-mark.svg](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/air-mark.svg). Use the variant from TASK-23: [docs/images/](https://github.com/ArborealML/Inference-Router/tree/main/docs/images/).
- Upload form 1 (card): [repo Settings → Options → Social preview](https://github.com/ArborealML/Inference-Router/settings/options).
- Upload form 2 (organisation picture): [organisation Settings → Profile picture](https://github.com/organizations/ArborealML/settings/profile).
- Confirm without the form: [the image link previews fetch](https://opengraph.githubassets.com/1/ArborealML/Inference-Router).
- Art provenance: [docs/images/README.md](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/README.md).

**Do this (about 4 minutes, or 8 if a converter has to be installed)**
1. **Export the card** (1280×640 is already its native size — the file declares `width="1280" height="640"`, so
   it opens at exactly the size GitHub asks for). Any **one** of these, wherever a converter lives:

   ```bash
   rsvg-convert -w 1280 -h 640 docs/images/air-social.svg -o air-social-1280x640.png   # librsvg2-bin
   magick -background none -density 96 docs/images/air-social.svg air-social.png        # ImageMagick
   inkscape -w 1280 -h 640 docs/images/air-social.svg air-social.png                    # inkscape
   ```

   No converter installed and none wanted: open
   <https://raw.githubusercontent.com/ArborealML/Inference-Router/main/docs/images/air-social.svg>
   in a browser and use DevTools → Command menu → **Capture node screenshot**; that yields a PNG at the rendered
   size. GitHub accepts PNG or JPEG up to 1 MB, and scales, so anything at least 1280 px wide is fine — the exact
   export is nicer, not required.

2. **Upload the card**: <https://github.com/ArborealML/Inference-Router/settings/options> → **Social preview** →
   *Upload image* → choose the PNG → **Save**.

3. **Export the mark — with one caveat that will bite otherwise.** `air-mark.svg` strokes in `currentColor` and
   declares no field, so rendered on its own it is **black on transparent**: an invisible mark on a dark avatar
   frame. Render the avatar-ready variant from TASK-23 (explicit `#10241F` field, `#F3F7F2` strokes) at 512×512,
   and don't upload the raw mark:

   ```bash
   rsvg-convert -w 512 -h 512 docs/images/air-mark-avatar.svg -o air-mark-512.png
   ```

4. **Upload the avatar**: <https://github.com/organizations/ArborealML/settings/profile> → *Profile picture* →
   upload the square PNG. GitHub crops to a circle, so nothing important may sit near the corners — the mark's
   three route dots reach the left and right edges of a 64×64 frame, so leave visible padding in the variant.

5. **Confirm from outside the form** (both caches, so give it a minute):
   - card: <https://opengraph.githubassets.com/1/ArborealML/Inference-Router> — this is the image link
     previews actually fetch, so if this shows our card, every share will;
   - avatar: the organisation page, and the owner chip beside the repository name.

6. **Answer in one line**, on this task:
   `TASK-29: card set, avatar set` — or `card set, avatar declined: <reason>` — or `both declined: <reason>`.

**What this waits on:** TASK-23, which owns the art judgement and the avatar-ready mark variant. If TASK-23 says
`replace` with a new direction, this task waits for the replacement art and nothing is uploaded in the meantime.

<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The social-preview image is set on the repository settings page, or declined with a one-line reason
- [x] #2 The organisation profile picture is set from the avatar-ready mark (TASK-23), or declined with a one-line reason
- [ ] #3 Both are confirmed from outside the settings form: the opengraph preview URL renders the new card, and the org page shows the new mark
<!-- AC:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T06:56:00Z

---

Answer received: `card set, avatar set`. Checked AC #2 (avatar) and NOT AC #1 (card), because only one of the two is true on the public side, and this task is graded on what a visitor sees, not on what was pressed.

**Avatar — confirmed, from outside the form.** `https://github.com/ArborealML.png?size=512` returns 460×460 RGB whose colour histogram is 82.4% near-black field and 16.5% near-white mark. Our export `air-mark-512.png` is 82.4% #10241F and 16.7% #F3F7F2 — the same ratio to a tenth of a percent, with GitHub re-encoding the palette slightly (#101010 / #F3F3F3 instead of #10241F / #F3F7F2). That is the mark.

**Card — not live.** Two independent checks: the repository page still serves `<meta property="og:image" content="https://repository-images.githubusercontent.com/1460450305/…">`, which is GitHubs **auto-generated repository image**, not an upload; and that images histogram is 87.6% white with 4.4% near-black — the inverse of our card, which is 93.6% #10241F field with 3.7% #F3F7F2 ink. A custom social preview would make the page point at `opengraph.githubassets.com/…` and serve the dark field. Re-checked after a pause, still the default.\n\nWhat to do: repo Settings → Options → Social preview → confirm the image is still shown in the box **and that Save was pressed** — the form silently keeps the previous state if Save is missed, and the removal/add state is easy to miss because the page shows a thumbnail either way. Then the check above should flip to the dark field; if you want the card ready to re-pick, the export is at `.local/exports/air-social-1280x640.png` (30,842 bytes, sha256 b02b6d32…97e9) and the mark at `.local/exports/air-mark-512.png` (9,935 bytes, sha256 2e1b5e02…820a). Stays Blocked: only a person can press that button.

---
<!-- COMMENTS:END -->
