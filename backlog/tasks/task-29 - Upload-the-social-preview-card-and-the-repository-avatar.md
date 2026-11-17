---
id: TASK-29
title: Upload the social preview card and the repository avatar
status: To Do
assignee:
  - '@claire'
created_date: '2026-11-17T05:59:00Z'
labels:
  - docs
dependencies: []
priority: low
ordinal: 29000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The one step in the identity work that no agent can do: the social-preview image and the repository avatar are set by an authenticated upload in repository Settings, with no API and no committed file that stands in for them. Everything around it is handled in repo: the vector source is committed, and TASK-23 exports the card to PNG or JPEG at the right size. This task exists so the human-only half shows up as one action instead of hiding inside a larger review task.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The exported social card is set in repository Settings, or declined with a reason
- [ ] #2 The mark is set as the repository or organisation avatar, or declined
<!-- AC:END -->
