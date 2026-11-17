---
id: TASK-29
title: Upload the social preview card and the organisation avatar
status: Blocked
assignee:
  - '@claire'
created_date: '2026-11-17T05:59:00Z'
updated_date: '2026-11-17T08:16:00Z'
labels:
  - docs
dependencies:
  - TASK-23
priority: low
ordinal: 23000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Two one-time uploads on GitHub that only a browser session can do. Everything up to the upload is done: the
vector sources are committed, and the two PNGs the settings forms want are committed in the repository, so the
upload is now download-and-pick, not export-and-hope.

**Set the social card** -> every link preview of the repository renders our card: dark evergreen field, the
branching-route mark, `AIR`, the name and the tagline. **Leave it** -> GitHub keeps rendering its own generated
card, a mostly-white box that says nothing about what this is. Today it renders GitHub's card (verified below).
**Set the organisation picture** -> the mark appears on the org page, on the owner chip beside the repository
name, and in search results. **Already done** and verified from outside the form.

**Why this task cannot be done by an agent:** both settings sit behind an authenticated web form. There is no
API, no `gh` endpoint, and no committed file that stands in for the upload. A session can prepare the exact PNG
and verify the result afterwards; it cannot press the button.

**Links**
- Card PNG to pick, 1280x640 as GitHub asks: [docs/images/air-social-1280x640.png](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/air-social-1280x640.png) (raw: [raw.githubusercontent.com/.../air-social-1280x640.png](https://raw.githubusercontent.com/ArborealML/Inference-Router/main/docs/images/air-social-1280x640.png)). 30,842 bytes, sha256 `b02b6d32...97e9`, well under the 1 MB ceiling.
- Avatar PNG (already uploaded, kept for a re-upload): [docs/images/air-mark-512.png](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/air-mark-512.png).
- Card vector source, if a different typeface is wanted: [air-social.svg](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/air-social.svg). The committed raster lettering is DejaVu Sans, not the browser face the SVG stack asks for.
- Do NOT upload the bare mark: [air-mark.svg](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/air-mark.svg) strokes in `currentColor` with no field, so standalone it is black on transparent and invisible on a dark frame. `air-mark-avatar.svg` / `air-mark-512.png` is the standalone form.
- Upload form 1 (card): [repo Settings -> Options -> Social preview](https://github.com/ArborealML/Inference-Router/settings/options).
- Upload form 2 (organisation picture): [organisation Settings -> Profile picture](https://github.com/organizations/ArborealML/settings/profile).
- Check the result from outside the form: [the image link previews fetch](https://opengraph.githubassets.com/1/ArborealML/Inference-Router), and the repository page `og:image` meta tag.
- Art provenance, renderer and resolved font: [docs/images/README.md](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/README.md).

**Do this (about 2 minutes)**
1. Download `docs/images/air-social-1280x640.png` (link above) or `git pull` and take it from the working tree.
2. Open [repo Settings -> Options -> Social preview](https://github.com/ArborealML/Inference-Router/settings/options), pick the PNG, then **press Save**. The form keeps showing the old state if Save is missed, which is the failure mode this task has now hit twice.
3. Confirm from either surface, after about a minute for cache: `curl -s https://github.com/ArborealML/Inference-Router | grep og:image`. While no custom card is set it prints `repository-images.githubusercontent.com/1460450305/...` (GitHub's generated image). Once a custom card is set it prints `opengraph.githubassets.com/<hash>/ArborealML/Inference-Router`, the same shape a repository with a custom card shows.
4. Answer in one line on this task: `TASK-29: card set` , `card declined: REASON`, or `cannot set it: REASON`.

**Current state, re-checked 2026-11-17 07:33 UTC.** Avatar live, card not live. See the comments for the
measurements and for what to look at in the form.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The social-preview image is set on the repository settings page, or declined with a one-line reason
- [x] #2 The organisation profile picture is set from the avatar-ready mark (TASK-23), or declined with a one-line reason
- [ ] #3 Both are confirmed from outside the settings form: the opengraph preview URL renders the new card, and the org page shows the new mark
- [ ] #4 Looking at the card at the upload moment: the three text lines read at full size and the mark does not compete with the wordmark; if it reads badly, name the typeface and the card is re-rendered (this is the human half of TASK-23 AC #3, moved here because this is where a person actually sees the image)
<!-- AC:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T06:56:00Z

---

Answer received: `card set, avatar set`. Checked AC #2 (avatar) and NOT AC #1 (card), because only one of the two is true on the public side, and this task is graded on what a visitor sees, not on what was pressed.

**Avatar — confirmed, from outside the form.** `https://github.com/ArborealML.png?size=512` returns 460×460 RGB whose colour histogram is 82.4% near-black field and 16.5% near-white mark. Our export `air-mark-512.png` is 82.4% #10241F and 16.7% #F3F7F2 — the same ratio to a tenth of a percent, with GitHub re-encoding the palette slightly (#101010 / #F3F3F3 instead of #10241F / #F3F7F2). That is the mark.

**Card — not live.** Two independent checks: the repository page still serves `<meta property="og:image" content="https://repository-images.githubusercontent.com/1460450305/…">`, which is GitHub's **auto-generated repository image**, not an upload; and that image's histogram is 87.6% white with 4.4% near-black — the inverse of our card, which is 93.6% #10241F field with 3.7% #F3F7F2 ink. A custom social preview would make the page point at `opengraph.githubassets.com/…` and serve the dark field. Re-checked after a pause, still the default.\n\nWhat to do: repo Settings → Options → Social preview → confirm the image is still shown in the box **and that Save was pressed** — the form silently keeps the previous state if Save is missed, and the removal/add state is easy to miss because the page shows a thumbnail either way. Then the check above should flip to the dark field; if you want the card ready to re-pick, the export is at `.local/exports/air-social-1280x640.png` (30,842 bytes, sha256 b02b6d32…97e9) and the mark at `.local/exports/air-mark-512.png` (9,935 bytes, sha256 2e1b5e02…820a). Stays Blocked: only a person can press that button.

---

created: 2026-11-17T07:45:00Z

---

Third check, 2026-11-17 07:33 UTC, and this one is decisive about what the settings page is holding. (a) The repository page still serves og:image = repository-images.githubusercontent.com/1460450305/..., and that object is 2269x1134 - GitHub's generated repository image. A repository that has a custom social preview serves og:image from opengraph.githubassets.com with a content hash in the path; checked against vitejs/vite as a positive control. (b) The object the link-preview endpoint serves is 1200x600 and 87.6 percent white with 3.9 percent purple (#7355DD, GitHub card furniture); our committed card is 93.6 percent #10241F field with 3.7 percent ink. Same answer from two surfaces, and re-fetching with different cache versions returns byte-identical bytes, so this is not a stale cache. Avatar by contrast is live: the org picture returns 460x460 at 82.4 percent #10241F and 16.5 percent #F3F7F2 against our export at 82.4/16.7 - that is the mark. What to look at: the card upload box in repo Settings -> Options, where an added image and an absent one both render as a thumbnail; the description answer has now said card set twice while the public surface says otherwise, which is the signature of pressing away from the form instead of Save. The PNG to pick is committed, so it is download-and-pick: docs/images/air-social-1280x640.png. AC #2 stays checked, AC #1 and #3 stay open, status stays Blocked: only a browser session can change that setting.

---
<!-- COMMENTS:END -->
