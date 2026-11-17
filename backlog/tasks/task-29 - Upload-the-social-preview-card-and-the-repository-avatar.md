---
id: TASK-29
title: Press Save on the social preview form (browser only)
status: Waiting
assignee:
  - '@claire'
created_date: '2026-11-17T05:59:00Z'
updated_date: '2026-11-17T13:30:00Z'
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
**Do this (2 minutes, browser only).** Open [repository Settings → Options → Social preview](https://github.com/ArborealML/Inference-Router/settings/options),
pick [docs/images/air-social-1280x640.png](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/air-social-1280x640.png)
(raw: [raw.githubusercontent.com/.../air-social-1280x640.png](https://raw.githubusercontent.com/ArborealML/Inference-Router/main/docs/images/air-social-1280x640.png)),
then **press Save** — the thumbnail looks the same whether you did or not. Then check from outside the form,
after about a minute for cache:

    curl -s https://github.com/ArborealML/Inference-Router | grep og:image

While no custom card is set that prints `repository-images.githubusercontent.com/1460450305/...` (GitHub's own
generated image); with one it prints `opengraph.githubassets.com/<hash>/ArborealML/Inference-Router`. As of
10:40Z it prints the first, so the upload is not live.

**Answer one line here:** `card set`, `card declined: REASON`, or `cannot set it: REASON`.

**Why this one stays with a person:** GitHub has no API and no `gh` endpoint for the social-preview image. An
agent can prepare the exact PNG and verify the public result, and has: the file is committed, 1280x640, and its
size and sha256 are recorded by [TASK-46](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-46%20-%20Render-the-final-social-preview-card-and-verify-it-from-outside-the-form.md). The avatar is already live and verified from outside the form — nothing to do there; see [TASK-23](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-23%20-%20Review-the-README-identity-and-the-social-preview-upload.md).

**If the card reads badly** at the moment you upload it — three text lines unreadable at full size, or the mark
competing with the wordmark — name the typeface in your answer and it goes back to TASK-46 for a re-render
instead of being uploaded twice.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The social-preview image is set on the repository settings page, or declined with a one-line reason
- [x] #2 The organisation profile picture is set from the avatar-ready mark (TASK-23), or declined with a one-line reason
- [ ] #3 Both are confirmed from outside the settings form: the opengraph preview URL renders the new card, and the org page shows the new mark
- [ ] #4 Looking at the card at the upload moment: the three text lines read at full size and the mark does not compete with the wordmark; if it reads badly, name the typeface and the card is re-rendered (this is the human half of TASK-23 AC #3, moved here because this is where a person actually sees the image)
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Reasoning and evidence moved out of the description under the answer-first rule (decision-15):

**Set the social card** -> every link preview of the repository renders our card: dark evergreen field, the
branching-route mark, `AIR`, the name and the tagline. **Leave it** -> GitHub keeps rendering its own generated
card, a mostly-white box that says nothing about what this is. Today it renders GitHub's card (verified below).
**Set the organisation picture** -> the mark appears on the org page, on the owner chip beside the repository
name, and in search results. **Already done** and verified from outside the form.

**Links**
- Card PNG to pick, 1280x640 as GitHub asks: [docs/images/air-social-1280x640.png](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/air-social-1280x640.png) (raw: [raw.githubusercontent.com/.../air-social-1280x640.png](https://raw.githubusercontent.com/ArborealML/Inference-Router/main/docs/images/air-social-1280x640.png)). 30,842 bytes, sha256 `b02b6d32...97e9`, well under the 1 MB ceiling.
- Avatar PNG (already uploaded, kept for a re-upload): [docs/images/air-mark-512.png](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/air-mark-512.png).
- Card vector source, if a different typeface is wanted: [air-social.svg](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/air-social.svg). The committed raster lettering is DejaVu Sans, not the browser face the SVG stack asks for.
- Do NOT upload the bare mark: [air-mark.svg](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/air-mark.svg) strokes in `currentColor` with no field, so standalone it is black on transparent and invisible on a dark frame. `air-mark-avatar.svg` / `air-mark-512.png` is the standalone form.
- Upload form 1 (card): [repo Settings -> Options -> Social preview](https://github.com/ArborealML/Inference-Router/settings/options).
- Upload form 2 (organisation picture): [organisation Settings -> Profile picture](https://github.com/organizations/ArborealML/settings/profile).
- Check the result from outside the form: [the image link previews fetch](https://opengraph.githubassets.com/1/ArborealML/Inference-Router), and the repository page `og:image` meta tag.
- Art provenance, renderer and resolved font: [docs/images/README.md](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/README.md).
<!-- SECTION:NOTES:END -->

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

created: 2026-11-17T10:58:00Z

---

Retitled and re-trimmed under decision-16: this is the **only** task left on a person, because GitHub has no API, no `gh` endpoint and no committed file that stands in for the social-preview upload — an agent can prepare the PNG and verify the result afterwards, but cannot press Save. Everything that is not the button moved to [TASK-46](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-46%20-%20Render-the-final-social-preview-card-and-verify-it-from-outside-the-form.md) (final render, measurements, sha256 of the file to pick) and the after-the-fact public check. Status: avatar live and verified; card not live — re-checked 10:40Z, the page still serves `repository-images.githubusercontent.com/1460450305/...`, which is GitHub's generated image, and a custom card would make it serve `opengraph.githubassets.com/...`. The form shows a thumbnail whether an image is set or not, so the failure mode is pressing away from the form instead of Save; that has now happened twice against a 'card set' answer.

---

created: 2026-11-17T13:30:00Z

---

**Fourth check, measured by the board at 8f78a73 (13:2xZ), and the card is still not live.** The repository page serves `og:image` = `repository-images.githubusercontent.com/1460450305/...`, GitHub's generated image. Fetched the link-preview object directly: 1200x600, 88.7% light pixels — our committed card is 96.1% dark field, 3.9% light, so the card GitHub shows is not ours. The avatar **is** live and re-confirmed independently: 460x460, 82.6% dark / 16.7% light against our export's 82.4/16.7.

Moved to **Waiting** rather than Blocked because there is now an event that wakes it: [TASK-46](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-46%20-%20Render-the-final-social-preview-card-and-verify-it-from-outside-the-form.md) is re-rendering the card, because the committed one fails its own legibility bar — its smallest text line has ~19 px caps in the 1280x640 raster and a GitHub link preview shows the card at about 600 px wide, so that line lands near 9 px. Uploading today's PNG is still open to anyone who wants a card now: it is 30,842 bytes, under GitHub's 1 MB ceiling, and replacing it later is a second trip to the same form with nothing burned. What the board does not want is a third upload of an image that fails the measurement in TASK-46 AC #2.

---
<!-- COMMENTS:END -->
