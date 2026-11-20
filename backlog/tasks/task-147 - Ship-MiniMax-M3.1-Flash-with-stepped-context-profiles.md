---
id: TASK-147
title: Ship MiniMax M3.1 Flash with stepped context profiles
status: Done
assignee:
  - '@gpt-6.2-sol-release'
created_date: '2026-11-20T20:59:00Z'
updated_date: '2026-11-20T22:56:00Z'
labels: []
milestone: m-4
dependencies: []
priority: high
ordinal: 145000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The [seven-tier MiniMax catalog](https://github.com/ArborealML/Inference-Router/pull/64) landed at 02e6c59. The shipped sample defaults to 96k, offers 64k as its lowest normal selection and retains 32k as an explicit fallback. Ten focused Release cases passed, including seven HY4 controls. Offline config validation produced the same bytes before and after integration. All three [post-merge checks](https://github.com/ArborealML/Inference-Router/actions/runs/44832080103) passed. Qualification covers the external catalog and offline routing against observed capacity. Hardware qualification remains separate.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 The shipped configuration resolves the canonical model and supported aliases to 32k, 64k, 96k, 128k, 256k, 512k and 1024k at 1024 tokens per k. An omitted profile selects 96k.
- [x] #2 Validate the shipped sample offline and test selection and forwarding against measured serving capacity, including an absent model and a request exceeding measured capacity.
- [x] #3 Document profile selection, distinguish configured tiers from selector support and hardware qualification, and preserve the historical 32k launch receipt.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Add the seven MiniMax profiles and verified aliases to the shared Maya sample. Document the default, the explicit fallback and the Maya reserve. Validate the edited sample and all seven existing HY4 projection rows. Verify MiniMax routing at measured 32k and 96k and an unloaded model with positive reported capacity. Require the exact no-owner refusal with no switch or extra completion POST. Preserve the historical 32k selector fixture.
<!-- SECTION:PLAN:END -->
