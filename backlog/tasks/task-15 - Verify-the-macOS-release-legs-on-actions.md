---
id: TASK-15
title: Verify the macOS release legs on Actions
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-16T22:30:00Z'
updated_date: '2026-11-17T06:00:00Z'
labels:
  - ci
milestone: v0.9 - native engine lifecycle
dependencies:
  - TASK-13
  - TASK-22
ordinal: 15000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
`release.yml` declares `osx-arm64` (macos-14) and `osx-x64` (macos-13) legs, and the Linux and
Windows legs have built and smoke-tested successfully on their own runners. The macOS jobs stayed
queued and were never picked up, which means this account has no usable macOS runner minutes (or
the image names need refreshing). Until one of those is fixed, the public repo advertises macOS
binaries it has never produced.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Decide: buy/enable macOS minutes, drop the macOS legs, or publish them unverified with a
  documented warning (owner decision)
- [ ] #2 If dropped, the workflow and README stop claiming osx artifacts
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Blocked on TASK-22 — the next move is macOS runner minutes and the release tag, which belongs to a human. Work here resumes when that task is closed.

Blocked-state check: this task is blocked because a person has to grant something (macOS runner minutes) and agree the first tag; the human task that carries both answers is TASK-22, now also recorded as a dependency so the board's own dependency view agrees with the prose.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T06:00:00Z

---

The decision in AC #1 is taken: **drop the macOS legs** — no paid runner minutes (decision-10). Two criteria removed rather than left as holes on a resolved task: the "if kept, one dry run completes both macOS legs green" branch can never be reached now, and "the runner image names are current for the account" stopped mattering the moment the legs went away (worth recording that they were never exercised, so nobody re-adds a leg from memory of the workflow file). What remains is AC #3 and it is code and documentation, not a decision: the workflow stops declaring and gating on osx artifacts, and the README platform table, the download copy and the checksum matrix name two platforms instead of four. Assignee is the code session because the next move is that edit.

---
<!-- COMMENTS:END -->

## Links

- Legs to remove: [release.yml:115-122](https://github.com/ArborealML/Inference-Router/blob/main/.github/workflows/release.yml#L115-L122).
- Gate that counts four assets: [release.yml:216-222](https://github.com/ArborealML/Inference-Router/blob/main/.github/workflows/release.yml#L216-L222).
- Copy claiming four platforms: [README.md:82-84](https://github.com/ArborealML/Inference-Router/blob/main/README.md#L82-L84) and [README.md:219-222](https://github.com/ArborealML/Inference-Router/blob/main/README.md#L219-L222).
- Evidence either way: [release workflow runs](https://github.com/ArborealML/Inference-Router/actions/workflows/release.yml), [releases](https://github.com/ArborealML/Inference-Router/releases). Command: `gh run list --workflow release.yml --limit 5`.

## Do this (about 1 minute)

The two answers this task needs (macOS runner minutes, and whether to cut the first tag) are on
**TASK-22**, in <https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/>. The build legs themselves are already written:
`.github/workflows/release.yml` builds four platform binaries and smoke-tests each on its own OS before
a release is created. What is missing is only the paid minutes and a tag.
