---
id: TASK-120
title: Keep CLI options out of the inference prompt
status: Done
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-18T20:16:00Z'
updated_date: '2026-11-19T15:40:00Z'
labels: []
milestone: m-0
dependencies: []
priority: high
ordinal: 118000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
[PR #33](https://github.com/ArborealML/Inference-Router/pull/33) landed at 1ea8157. Both CLI inference verbs exclude recognized option operands, preserve positional text and read stdin when only options remain. SPEC section 11 documents the direct HTTP client and message contract. The feature review accepted bf35e24; the merge review accepted 73bd921 with identical CLI and test blobs. Existing handling of unsupported dash-prefixed text and the inert since option remains scoped as documented in the review.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Both air chat and complete send exactly the positional user text with named options. A subprocess test inspects the captured request and verifies system instructions remain in the system message.
- [x] #2 With options and no positional prompt, both verbs read stdin. Option order preserves the documented request behavior.
- [x] #3 SPEC section 11 describes the shipped CLI boundary and prompt rules.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Limit the correction to prompt extraction. Exclude all eligible operands of recognized chat options, including duplicates and the inert since option. Preserve first valid lookup, option flags and positional order. Read trimmed stdin only when positional text is absent. Verify both verbs through the real CLI and a loopback capture; cover option interleaving, duplicates, missing operands, default streaming and exact messages. Align SPEC section 11 with the actual HTTP path.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
All 44 focused Release subprocess cases passed on bf35e24, including four exact-request cases across both verbs and both input modes. The [final hosted checks](https://github.com/ArborealML/Inference-Router/actions/runs/44556522206) passed on the reconciled head.
<!-- SECTION:NOTES:END -->
