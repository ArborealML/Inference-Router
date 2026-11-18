---
id: TASK-95
title: Let the shipped binary name its own version and the commit it came from
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T08:38:00Z'
labels:
  - release
  - cli
dependencies: []
priority: medium
ordinal: 94000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Measured at 08:37Z on 27ea38b: the CLI has no version at all. grep for version in the CLI Program.cs returns nothing; no Version, VersionPrefix or InformationalVersion appears in any csproj, Directory.Build.props or global.json; the verbs are chat, complete, config, events, help, models, serve, status, switch, threads, tickets. So the version numbers this project writes into commit messages and SPEC rows exist only in prose — the binary a downloader receives cannot say what it is, and a published artifact cannot be matched to its tag by inspecting the artifact. That is the cheap half of proving what a downloader receives: the digest work sits on TASK-48, which cannot close the question of identity on its own. Cost is small: one stamp at build time, one flag, one test. Nothing waits on this row, and it is second in the release area, behind the attestation work on TASK-48.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The CLI prints a version and the commit the binary was built from, from a build-time stamp, with no runtime reflection and no warning under TreatWarningsAsErrors.
- [ ] #2 The stamp comes from the tag or checked-out commit in the same workflow that builds and publishes, so a published binary and a tag can be matched by running the binary.
- [ ] #3 SPEC names the surface, and one pin shows a built binary's reported commit matching what the run recorded for that artifact.
<!-- AC:END -->
