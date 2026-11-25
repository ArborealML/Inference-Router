---
id: TASK-188
title: Make the Pi extension that runs be the Pi extension that is reviewed
status: To Do
assignee:
  - '@claire'
created_date: '2026-11-25T10:50:00Z'
labels: []
dependencies: []
priority: high
ordinal: 180000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The Pi-side AIR extension is tracked in this repository under tools/pi-air, but the copy actually loaded by the running Pi sessions is a different file in the operator's inference-setup tree. Verified today: the tracked copy sends the serving-profile header and injects no capacity warning; the installed copy does the opposite and caps output at 32768 tokens, or 65536 for one model family. Every review, diff and test run against the tracked copy therefore describes a client that is not in use, and incident reports about the running client cannot be reproduced from the repository. Decide the authoritative copy, install from it, and prove the running copy matches.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The extension loaded by a running Pi session is byte-identical to a commit in this repository.
- [ ] #2 The proof is a hash comparison shown in the task, not a file name.
- [ ] #3 The launcher installs from the tracked copy rather than a private tree.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Came out of the capacity-warning diagnosis. This blocks trusting any review of client behaviour, including the profile and priority work.
<!-- SECTION:NOTES:END -->
