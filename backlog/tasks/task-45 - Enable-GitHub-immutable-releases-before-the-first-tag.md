---
id: TASK-45
title: Enable GitHub immutable releases before the first tag
status: Waiting
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T10:52:00Z'
updated_date: '2026-11-18T20:57:00Z'
labels:
  - ci
  - release
  - security
milestone: m-9
dependencies: []
priority: medium
type: task
ordinal: 44000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Performed before the first tag;** the calls are recorded for anyone re-running them:

    gh api repos/ArborealML/Inference-Router/immutable-releases
    gh api -X PUT repos/ArborealML/Inference-Router/immutable-releases
    gh api repos/ArborealML/Inference-Router/immutable-releases

The enable is `PUT` on the hyphenated path (an underscore spelling 404s) and needs repository admin, which the working token has. This changes a public repository's supply-chain posture and `DELETE` on the same path reverses it; it covers only releases published after it is turned on.

**What it locks, corrected by measurement at 19:2xZ**: the version **name** — delete the release and the name stays burned
(`422 tag_name was used by an immutable release`). Not the assets: on a published immutable release an asset was deleted
and a different file uploaded under the same name, and the download URL serves the new bytes. Not the tag ref: with no
ruleset `git push -f` moved a published immutable release's tag. Metadata stays mutable (`PATCH` 200).

**What it does not lock** — corrections landed in SPEC v0.12.20 and by measurement: drafts, the window before publication, the release title and notes, the `prerelease`/`latest` flags; and **the release itself can be deleted, after which the tag can be deleted too — but the version name is burned for good**, and turning the setting off later does not unlock it So there is no disposable canary once this is on ([TASK-42](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-42%20-%20Run-the-canary-through-the-real-gate-knowing-the-version-name-is-burned.md)): it seals bad bytes as well as good ones, and it is not the tag rule in [TASK-38](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-38%20-%20Act-put-a-v-ruleset-on-the-repository-and-read-it-back.md).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 immutable releases are enabled over REST (or refused with a written reason), and the before/after state is quoted from the API rather than asserted
- [ ] #2 SPEC says plainly what this does and does not lock: it burns the published version name, and locks nothing measured about assets, the tag ref, drafts, the pre-publish window, the title and notes, or the prerelease/latest flags
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**Report line (decision-16), performed.** Call: `gh api -X PUT repos/ArborealML/Inference-Router/immutable-releases`.
Before, at 8f78a73: `{"enabled":false,"enforced_by_owner":false}`. After, read live at 20:0xZ: `{"enabled":true,"enforced_by_owner":false}`.
Blast radius in one sentence: every future release on this public repository keeps its published version name burned, while
drafts, metadata, assets and the tag ref stay reachable and the release itself can still be deleted. Reversal: `DELETE` on the
same path. AC #1 closes on those two quotes. **Wake:** the SPEC wording pass AC #2 asks for (the coding session's file).

**AC #1 done, read live at 20:0xZ:** `GET .../immutable-releases` answers `{"enabled":true,"enforced_by_owner":false}`;
the before state (`enabled: false`) is quoted above from 8f78a73. AC #2 is the remaining work: SPEC has to say plainly, in
current-state prose rather than only in a revision-log row, that this locks the version name and nothing measured about
assets or the tag ref. **Wake:** that SPEC wording pass, which belongs to the coding session.

**Org-level boundary, measured at 8f78a73:** `GET orgs/ArborealML/settings/immutable-releases` and `GET orgs/ArborealML/rulesets` need the `admin:org` scope, so organisation-wide immutability is a person's act. Nothing waits on it; the repository-level setting answers `admin: true`.

Superseded log entries for this task: [board trim, 2026-11-17](https://github.com/ArborealML/Inference-Router/blob/main/backlog/archive/board-trim-2026-11-17.md).

SPEC release acceptance at 4dd1e0e documents the asset replacement, tag ref, draft and metadata measurements. Verify the remaining prerelease/latest flag wording against recorded evidence before closing criterion 2.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
Historical sequencing: the laboratory disposition preceded enabling immutable releases.
<!-- COMMENTS:END -->
