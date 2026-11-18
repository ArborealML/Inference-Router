---
id: TASK-29
title: Upload the social preview card and the repository avatar
status: Done
assignee:
  - '@claire'
created_date: '2026-11-17T05:59:00Z'
updated_date: '2026-11-18T19:29:00Z'
labels:
  - docs
milestone: m-9
dependencies:
  - TASK-23
  - TASK-46
priority: low
ordinal: 23500
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Closed: the card is live, and the "not working" reading was our test, not the upload.** GitHub applies a
social-preview image the moment the file is accepted — that pane has no Save control — and it serves a custom
upload from the same host as its own generated card, so a URL host proves nothing. The check that decides is
the bytes: re-measured 2026-11-18T05:50Z, the repository page's `og:image` resolves to a
`repository-images.githubusercontent.com/1460450305/…` address that fetches as 38,150 bytes, 1280x640, sha256
`cee3237b7f98…98f09` — byte-identical to
[docs/images/air-social-1280x640.png](https://github.com/ArborealML/Inference-Router/blob/main/docs/images/air-social-1280x640.png)
as committed at b214ce6. The organisation picture is live too, verified from outside the form (TASK-23). No
further act is needed here.

For the next browser-only setting: hand the owner the file, its size and sha256, and the one command that
fetches what the public page serves and hashes it. Do not ask for a Save press, and do not read a host name as
the verdict — that is what turned three correct uploads into three reported failures.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 The social-preview image is set, or declined with a one-line reason, and the answer line is in this task
- [x] #2 The organisation picture is set from the avatar-ready mark (TASK-23), or declined with a one-line reason
- [x] #3 The `og:image` meta tag on the public repository page prints `opengraph.githubassets.com`, quoted here
- [x] #4 The picked file's sha256 is recorded here and equals the committed card's `cee3237b7f98…98f09`
- [x] #5 At the upload moment the three text lines read at full size and the mark does not compete with the wordmark; if it reads badly, name the typeface and the card goes back for one re-render
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**The wrong test, named.** This row carried a host-name discriminator (`opengraph.githubassets.com` = ours,
`repository-images.githubusercontent.com` = GitHub's generated card) and a "Press Save" step. Both are false:
GitHub applies the upload on accept, and serves a custom image from `repository-images.githubusercontent.com`
under a random path. That premise, not the owner's uploads, produced three "still not taken" readings. TASK-46
AC #5 was reworded on that finding at 03:23Z; this row's steps are corrected at 05:5xZ. The standing rule is
decision-38, and the contract now carries it.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T23:20:00Z

---

curl -s https://github.com/ArborealML/Inference-Router | grep -o '<meta property="og:image"[^>]*>'

---
<!-- COMMENTS:END -->
