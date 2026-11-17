---
id: TASK-44
title: Close what the seventh release-gate review found (over v0.12.15)
status: Waiting
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T10:52:00Z'
updated_date: '2026-11-17T11:33:00Z'
labels:
  - ci
  - release
  - security
dependencies: []
priority: high
type: task
ordinal: 43000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Wake: the eighth deep review of the gate, dispatched 11:25Z over fd76021.** Carrier for the seventh review
(over v0.12.15). Four of its items are implemented in aa7ad23, each with a harness case: fields compared as JSON
with typed `jq -e`, expected values passed as jq arguments; the asset
list validated for length, name characters and field types before anything is parsed; the case-fold narrowed to
scheme/host/owner-repo, path byte-exact from the release id on; and the un-draft `PATCH` response read as a
release object, its failure text not claiming nothing shipped. A hostile release title reaches the log escaped (pub_log_injection).

**One suggested hardening was rejected with counter-evidence** (fd76021, SPEC §14): passing
`target_commitish=<pinned sha>` on the create. The endpoint note makes the workflow-write guard a function of the
*resolved* target relative to the default branch, and the Actions `GITHUB_TOKEN` can never satisfy that guard.
Omitting the parameter resolves to the default-branch head, where that diff is empty by construction; naming the
tag commit makes the answer depend on a comparison this run cannot perform before the call. So it is not sent, and
the question becomes canary evidence.

**What stays open is authorization, not parsing:** (i) that `contents: write` from `GITHUB_TOKEN` suffices in
practice for upload plus un-draft — docs say yes, no run has; (ii) whether the workflow-write guard fires for a
tag cut at the head of main. Both need the canary in TASK-42, and our own tag commits touch release.yml.

**Harness counts are as-of.** Measured by the board session at fd76021: **73 cases, 0 failures** re-run. Any 52 or 59 elsewhere on the board belongs to its own round. No criterion inside the release-job
steps should be written as a line number: those steps moved twice this week.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 upload_url is compared case-insensitively only over scheme, host and owner/repo, with the path from the release id onward compared byte-exact; the literal assets{?name,label} template suffix is required and cut at the brace; an already-expanded or unexpected query is refused (done in aa7ad23; harness case pub_url_path_case proves /Assets refuses)
- [x] #2 the target_commitish hardening is NOT taken and the reason is on the record: the endpoint note makes the workflow-write guard a function of the RESOLVED target relative to the default branch, and the Actions GITHUB_TOKEN can never be authorized for it, so naming the tag commit turns a comparison that is empty by construction into one this run cannot evaluate before the call; what remains is canary evidence — record what .target_commitish says after a canary create — filed on TASK-42 (reasoning in fd76021 and SPEC 14)
- [x] #3 the un-draft PATCH response is read and checked as a release object, and its failure text does not imply nothing shipped (done in aa7ad23; case pub_patch_answers_draft shows rc!=0 while the release is public)
- [ ] #4 two things about authorization stay unproven and are stated as such rather than argued away: (i) that contents: write from GITHUB_TOKEN is sufficient in practice for asset upload plus un-draft — the docs say yes, no run has shown it; (ii) whether the workflow-write guard fires at all for a tag cut at the head of main, which by the note above should be no, and only a live run shows it. Both wake on the canary in TASK-42, and our own tag commits touch release.yml, which is an argument for running it
- [ ] #5 when immutable releases are enabled, the gate reads .immutable on the published release and records it, and SPEC states what immutability does not lock: drafts, the pre-publish window, the title and notes body, and the prerelease/latest flags
- [x] #6 SPEC carries the disposition row for this review naming each finding's disposition, with the revision-log line in the same commit as the code (the v0.12.16 row is on main)
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Disposition of the seventh review's findings, by commit, so a later reader can
tell a decision from an oversight.

**Fixed in aa7ad23** (code + harness): response fields are compared as JSON with `jq -e` and typed equality, never
as columns of a `join(" ")` string re-split by `read` — a null collapses and a space inside a value becomes a
separator, so a create answer of `name "v0.1.0 true"` with `draft` absent used to pass four comparisons at once,
and a cleanup lookup answered the same way would have deleted a **published** release. An asset named
`"<wanted> uploaded <size> <digest>"` used to report its own metadata into the compared columns. The asset list is
now required to be well-formed (length equals expected count, names only `[A-Za-z0-9.-]`, typed state and size,
digest absent-or-sha256) before any of it is parsed. Case-folding narrowed to scheme/host/owner-repo. The un-draft
`PATCH` response is read and checked, with failure text that does not imply nothing shipped. A response's own text
is escaped and truncated before it reaches stdout.

**Not taken, with reasoning (fd76021, SPEC §14):** `target_commitish=<pinned sha>` on the create — see AC #2. The
reviewer's claim that an absent tag lets GitHub create it on the default branch is true; the repair that follows
from the note is not the one proposed.

**Deferred to the canary (TASK-42):** `.target_commitish` as answered by a real create; whether the workflow-write
guard fires for a tag at the head of main; whether `contents: write` from `GITHUB_TOKEN` is sufficient in practice.

**Deferred to TASK-45:** reading `.immutable` after publication, which cannot be tested until immutability is on.

**Harness trap found by the harness itself**, worth repeating: `${var:0:$n,,}` is an arithmetic error in bash that
does not abort a `set -euo pipefail` step, and it made six upload_url cases pass for the wrong reason.
<!-- SECTION:NOTES:END -->
