---
id: TASK-23
title: 'Review: the README identity, and the social-preview upload'
status: To Do
assignee:
  - '@claire'
created_date: '2026-11-17T02:26:00Z'
updated_date: '2026-11-17T05:27:00Z'
labels:
  - docs
dependencies: []
ordinal: 23000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
A mark, a banner and a wider social card were generated as vector art and committed under docs/images/, with the banner at the top of the README. Look at them and say keep or replace. Two things only you can do: the social preview has to be exported to PNG or JPEG and pasted into the repository settings on GitHub, and if the mark should also be the organisation or repository avatar, that is your upload. The palette is dark evergreen (#10241F field, #2B806C accent); if you want a different colour, it is a three-file change and no code is involved.

What each answer changes: **keep** leaves the committed art as it is and leaves only the settings upload; **replace with a direction** sends the direction to the generator and the three files are recommitted — palette changes included, no code touched. What it unblocks: TASK-14, the README work — its identity half is closed the moment you say keep, and a replacement direction reopens it. Its last open acceptance criterion (a release badge that resolves) does not wait on this task: it waits on a release tag, which is TASK-22.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The mark and banner are kept, or replaced with a direction you give
- [ ] #2 The social preview is exported and set in the repository settings, or declined
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Board audit: the steps, the cost, the upload that only a human can do and the answer format were already here. Added the consequences of each option and what this task unblocks, which the working contract requires on a human task.
<!-- SECTION:NOTES:END -->

## Do this (about 3 minutes)

1. Open `docs/images/air-banner.svg` and `docs/images/air-mark.svg` in a browser, or just look at
   <https://github.com/ArborealML/Inference-Router> — the banner is at the top of the README.
2. Say `keep` or `replace with <direction>`. The palette is in `docs/images/README.md`; a colour change
   is a three-file edit and I can do it in minutes.
3. One thing only a human can do: GitHub's social card is an upload, not a committed file. Repo
   **Settings → Options → Social preview**, upload a PNG or JPEG exported from `docs/images/air-social.svg`
   (1280×640). I cannot export or set it.

Answer format: `TASK-23: keep, card set` or `TASK-23: replace: <direction>`.
