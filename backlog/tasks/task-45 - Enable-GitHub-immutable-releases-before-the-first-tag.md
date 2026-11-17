---
id: TASK-45
title: Enable GitHub immutable releases before the first tag
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T10:52:00Z'
updated_date: '2026-11-17T11:06:00Z'
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
**Do this (~10 minutes, two API calls).** Check the state, enable it, check again:

    gh api repos/ArborealML/Inference-Router/immutable-releases
    gh api -X PUT repos/ArborealML/Inference-Router/immutable-releases
    gh api repos/ArborealML/Inference-Router/immutable-releases

Measured on this repository today the first line answers `{"enabled":false,"enforced_by_owner":false}`; the
documented enable is `PUT` on the same path and needs admin on the repository, which the working token has.
Quote before-and-after JSON in the report — this changes a public repository's supply-chain posture, so it has
to be visible, and `DELETE` on the same path reverses it.

**Do it before the first tag**, because it covers only releases published after it is turned on.

**What it locks**: a published release's assets (no add, replace or delete) and its associated tag (no move or
delete while the release exists), plus attestations over tag, commit and assets.
**What it does not lock**: drafts, the window before publication, the release title and notes body, the
`prerelease`/`latest` flags. A release published in a wrong state is frozen wrong. The release itself can still
be deleted, after which the tag name is not reusable — so this is not availability, and it is not a build
provenance guarantee. It is not a substitute for the tag rule in
[TASK-38](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-38%20-%20Decision-protect-release-tags-and-decide-who-may-publish-releases.md).

**Links**: [GitHub: immutable releases](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases);
the gate it protects: [.github/workflows/release.yml](https://github.com/ArborealML/Inference-Router/blob/main/.github/workflows/release.yml); the release it must precede: [TASK-28](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-28%20-%20Cut-the-first-release-tag-and-watch-the-release-run.md).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 immutable releases are enabled over REST (or refused with a written reason), and the before/after state is quoted from the API rather than asserted
- [ ] #2 SPEC says plainly what this does and does not lock: it locks a published release's assets and its tag, and nothing about drafts, the pre-publish window, the title and notes, or the prerelease/latest flags
- [ ] #3 the canary in [TASK-42](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-42%20-%20Decision-run-a-canary-release-to-prove-the-release-gate-exists.md) tries an asset replacement and a tag move after publication and requires both to be refused
<!-- AC:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T11:06:00Z

---

TASK-39's answer makes this a prerequisite rather than an option, and sharpens what to write in SPEC: immutability locks published assets and the tag while the release exists, and it does not lock drafts, the pre-publish window, the title or notes, or the prerelease/latest flags — and it will freeze a malicious set just as faithfully as a good one. Its attestation binds the GitHub-published release set, not the trustworthiness of the source, the approval, or the build execution.

---
<!-- COMMENTS:END -->
