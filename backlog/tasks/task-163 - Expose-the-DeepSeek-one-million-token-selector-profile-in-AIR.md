---
id: TASK-163
title: Expose the DeepSeek one-million-token selector profile in AIR
status: To Do
assignee:
  - '@gpt-6.2-sol-lifecycle'
created_date: '2026-11-22T03:06:00Z'
labels: []
dependencies: []
priority: high
ordinal: 158000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Port the existing single-slot max profile as an explicit Pi context choice while keeping the normal default at 256k.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The optional 1m profile selects 1048576 tokens and one slot; the default stays 262144 tokens.
- [ ] #2 Verify configured discovery and independently measure the served window and slots on a completed live request.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Port the existing single-slot max profile as an explicit Pi context choice while keeping the normal default at 256k.
<!-- SECTION:PLAN:END -->
