---
id: TASK-45
title: Enable GitHub immutable releases before the first tag
status: Blocked
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T10:52:00Z'
updated_date: '2026-11-17T13:56:00Z'
labels:
  - ci
  - release
  - security
dependencies: []
priority: medium
type: task
ordinal: 44000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Do this (~10 minutes, two API calls, before the first tag).** Check the state, enable it, check again:

    gh api repos/ArborealML/Inference-Router/immutable-releases
    gh api -X PUT repos/ArborealML/Inference-Router/immutable-releases
    gh api repos/ArborealML/Inference-Router/immutable-releases

Measured by this session at 8f78a73 the first line answers `{"enabled":false,"enforced_by_owner":false}`; the documented enable is `PUT` on the same hyphenated path (an underscore spelling 404s) and needs repository admin, which the working token has. Quote before-and-after JSON — this changes a public repository's supply-chain posture, and `DELETE` on the same path reverses it. It covers only releases published after it is turned on.

**What it locks**: a published release's assets (no add, replace or delete) and its associated tag (no move or delete while the release exists), plus attestations over tag, commit and assets.

**What it does not lock** — one of these is a correction to what this board said earlier, landed in SPEC v0.12.20 against GitHub's own OpenAPI description: drafts, the window before publication, the release title and notes, the `prerelease`/`latest` flags; and **the release itself can be deleted, after which the tag can be deleted too — but the version name is burned for good**, and turning the setting off later does not unlock it. So there is no disposable canary once this is on ([TASK-42](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-42%20-%20Decision-prove-the-release-gate-on-a-canary-knowing-the-tag-name-is-burned.md)), immutability can seal bad bytes as well as good ones, and this is not availability and not a build-provenance guarantee. It is not a substitute for the tag rule in [TASK-38](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-38%20-%20Decision-protect-release-tags-and-decide-who-may-publish-releases.md).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 immutable releases are enabled over REST (or refused with a written reason), and the before/after state is quoted from the API rather than asserted
- [ ] #2 SPEC says plainly what this does and does not lock: it locks a published release's assets and its tag, and nothing about drafts, the pre-publish window, the title and notes, or the prerelease/latest flags
- [ ] #3 the canary in [TASK-42](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-42%20-%20Decision-prove-the-release-gate-on-a-canary-knowing-the-tag-name-is-burned.md) tries an asset replacement and a tag move after publication and requires both to be refused
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Report line owed by whoever performs this (decision-16): the exact call used — `gh api -X PUT repos/ArborealML/Inference-Router/immutable-releases` — the before and after JSON from the GET, the blast radius in one sentence (every future release on this public repository: published assets and their tag locked, drafts and metadata still editable, release deletion still possible), and the reversal (DELETE on the same path). Blast radius is repository-wide, so the report is not optional.

**Org-level boundary, measured at 8f78a73:** `GET orgs/ArborealML/settings/immutable-releases` and `GET orgs/ArborealML/rulesets` both answer "This API operation needs the `admin:org` scope", so organisation-wide immutability is out of reach for this token and would be a person's act. Nothing here waits on it: the repository-level setting is readable and writable today (`enabled: false`, `enforced_by_owner: false`, and the repository answers `admin: true`).
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T11:06:00Z

---

TASK-39's answer makes this a prerequisite rather than an option, and sharpens what to write in SPEC: immutability locks published assets and the tag while the release exists, and it does not lock drafts, the pre-publish window, the title or notes, or the prerelease/latest flags — and it will freeze a malicious set just as faithfully as a good one. Its attestation binds the GitHub-published release set, not the trustworthiness of the source, the approval, or the build execution.

---

created: 2026-11-17T11:31:00Z

---

Ownership, settled: the coding session reads this as an owner Decision because it is a
repository setting and nothing in YAML. It is not an owner act here: it is a REST call from a machine whose
working token already answers `admin: true` for this repository (measured today), so under decision-16 it is
agent work — `gh api -X PUT repos/ArborealML/Inference-Router/immutable-releases`, then the same GET to prove the
state moved. What makes it reportable rather than private: it changes a public repository's supply-chain posture,
it reverses with `DELETE`, and it must be visible in SPEC and in the commit message before any release is published.
The owner keeps the veto, not the keystrokes.

---

created: 2026-11-17T13:56:00Z

---

Blocked on TASK-49 (a): enabling immutable releases makes a canary permanent-by-name, so the canary answer comes first. The enable itself is two API calls and measured readable today (`enabled: false`).

---
<!-- COMMENTS:END -->
