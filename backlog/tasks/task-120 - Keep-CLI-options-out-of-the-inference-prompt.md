---
id: TASK-120
title: Keep CLI options out of the inference prompt
status: To Do
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-18T20:16:00Z'
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
