---
id: TASK-163
title: Expose the DeepSeek one-million-token selector profile in AIR
status: Testing
assignee:
  - '@gpt-6.2-sol-lifecycle'
created_date: '2026-11-22T03:06:00Z'
updated_date: '2026-11-22T03:12:00Z'
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The selector calls its single-slot 1048576-token profile max. Verified the protected argument set differs from 32k only in context, added the optional 1m tier to the local AIR catalog and passed offline config validation. Hardware readback remains pending.
<!-- SECTION:NOTES:END -->
