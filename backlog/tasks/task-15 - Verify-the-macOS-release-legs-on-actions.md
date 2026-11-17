---
id: TASK-15
title: Verify the macOS release legs on Actions
status: Blocked
assignee:
  - '@claire'
created_date: '2026-11-16T22:30:00Z'
updated_date: '2026-11-17T02:19:00Z'
labels:
  - ci
milestone: v0.9 - native engine lifecycle
dependencies:
  - TASK-13
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
- [ ] #1 Decide: buy/enable macOS minutes, drop the macOS legs, or publish them unverified with a
  documented warning (owner decision)
- [ ] #2 If kept, one dry run completes both macOS legs green, including the smoke test
- [ ] #3 If dropped, the workflow and README stop claiming osx artifacts
- [ ] #4 The runner image names used are current for the account (macos-13 availability is the risk)
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Blocked on TASK-22 — the next move is macOS runner minutes and the release tag, which belongs to a human. Work here resumes when that task is closed.
<!-- SECTION:NOTES:END -->

## Do this (about 1 minute)

The two answers this task needs (macOS runner minutes, and whether to cut the first tag) are on
**TASK-22**, in <https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/>. The build legs themselves are already written:
`.github/workflows/release.yml` builds four platform binaries and smoke-tests each on its own OS before
a release is created. What is missing is only the paid minutes and a tag.
