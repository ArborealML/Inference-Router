---
id: TASK-120
title: Keep CLI options out of the inference prompt
status: Preparing
assignee:
  - '@gpt-6.2-sol-release'
created_date: '2026-11-18T20:16:00Z'
updated_date: '2026-11-19T15:11:00Z'
labels: []
milestone: m-0
dependencies: []
priority: high
ordinal: 118000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Review of the native walkthrough at 05fe144 identified a CLI defect. RunChatAsync treats each option value as user text, including transport addresses and system instructions, and can bypass stdin. SPEC section 11 also describes a wrapper although the CLI constructs and reads HTTP requests directly.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Both air chat and complete send exactly the positional user text with named options. A subprocess test inspects the captured request and verifies system instructions remain in the system message.
- [ ] #2 With options and no positional prompt, both verbs read stdin. Option order preserves the documented request behavior.
- [ ] #3 SPEC section 11 describes the shipped CLI boundary and prompt rules.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
The Release implementer prepares the CLI correction while the independent plan review dispatched November 19 runs. Preserve documented options for both verbs, exclude consumed option values from the positional prompt and read stdin when positional text is absent. Verify exact messages with a loopback subprocess capture and align SPEC section 11 with the shipped HTTP client.
<!-- SECTION:PLAN:END -->
