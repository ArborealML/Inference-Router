---
id: TASK-147
title: Ship MiniMax M3.1 Flash with stepped context profiles
status: Waiting
assignee:
  - '@gpt-6.2-sol-release'
created_date: '2026-11-20T20:59:00Z'
labels: []
milestone: m-4
dependencies: []
priority: high
ordinal: 145000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wake on independent plan review of the shipped Maya catalog change on bbd8bb9. Delivery will check alias forwarding and measured capacity before implementation. Ship the same context ladder as HY4 for MiniMax M3.1 Flash. Treat 64k as the lowest normal selection, 96k as the normal default and 32k as an explicit fallback.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The shipped configuration resolves the canonical model and supported aliases to 32k, 64k, 96k, 128k, 256k, 512k and 1024k at 1024 tokens per k. An omitted profile selects 96k.
- [ ] #2 Validate the shipped sample offline and test selection and forwarding against measured serving capacity, including an absent model and a request exceeding measured capacity.
- [ ] #3 Document profile selection, distinguish configured tiers from selector support and hardware qualification, and preserve the historical 32k launch receipt.
<!-- AC:END -->
