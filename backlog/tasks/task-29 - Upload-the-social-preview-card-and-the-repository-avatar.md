---
id: TASK-29
title: Press Save on the social preview form (browser only)
status: Blocked
assignee:
  - '@claire'
created_date: '2026-11-17T05:59:00Z'
updated_date: '2026-11-17T15:54:00Z'
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
- [ ] #4 At the upload moment the three text lines read at full size and the mark does not compete with the wordmark; if it reads badly, name the typeface and the card is re-rendered
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
- Card PNG to pick, 1280x640 as GitHub asks: [docs/images/air-social-1280x640.png](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/air-social-1280x640.png) (raw: [raw.githubusercontent.com/.../air-social-1280x640.png](https://raw.githubusercontent.com/ArborealML/Inference-Router/main/docs/images/air-social-1280x640.png)). 38,150 bytes, sha256 `cee3237b7f98…98f09` as committed in b214ce6, well under the 1 MB ceiling — re-read the hash from the file at upload time, because TASK-49 item (d) can still change it.
- Avatar PNG (already uploaded, kept for a re-upload): [docs/images/air-mark-512.png](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/air-mark-512.png).
- Card vector source, if a different typeface is wanted: [air-social.svg](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/air-social.svg). The committed raster lettering is DejaVu Sans, not the browser face the SVG stack asks for.
- Do NOT upload the bare mark: [air-mark.svg](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/air-mark.svg) strokes in `currentColor` with no field, so standalone it is black on transparent and invisible on a dark frame. `air-mark-avatar.svg` / `air-mark-512.png` is the standalone form.
- Upload form 1 (card): [repo Settings -> Options -> Social preview](https://github.com/ArborealML/Inference-Router/settings/options).
- Upload form 2 (organisation picture): [organisation Settings -> Profile picture](https://github.com/organizations/ArborealML/settings/profile).
- Check the result from outside the form: [the image link previews fetch](https://opengraph.githubassets.com/1/ArborealML/Inference-Router), and the repository page `og:image` meta tag.
- Art provenance, renderer and resolved font: [docs/images/README.md](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/README.md).

**Fourth measurement, this session, at b214ce6:** the repository page still serves `og:image` = `repository-images.githubusercontent.com/1460450305/...`, and the link-preview object is 1200x600 at 88.7% light pixels against our card's 4.8% — GitHub is still showing its own generated image, so the upload has not taken. The organisation avatar **is** live and re-confirmed: 460x460, 82.6% dark / 16.7% light against our export's 82.4/16.7.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->

created: 2026-11-17T13:56:00Z

---

**Nothing in the repository is outstanding for this task, so it is Blocked on a person again, not Waiting.** The card is committed and measured (b214ce6: 1280x640, 38,150 B, sha256 `cee3237b7f98…98f09`; smallest text line 42 px, four times the previous card's 20 px). What is missing is the upload: repo Settings -> Options -> Social preview, pick the committed `docs/images/air-social-1280x640.png`, then **press Save** — the form shows a thumbnail either way, which is how three 'card set' answers have produced no change. One optional answer can still change the image first: TASK-49 item (d).

---

<!-- COMMENTS:END -->
