---
id: TASK-97
title: >-
  Stop the admin ticket snapshot echoing caller identifiers at a length nothing
  bounds
status: Blocked
assignee:
  - '@gpt-6.2-sol-stream-delivery'
created_date: '2026-11-18T10:57:00Z'
updated_date: '2026-11-20T18:38:00Z'
labels: []
milestone: m-1
dependencies:
  - TASK-145
ordinal: 96000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Blocked on Claire’s decision in TASK-145 about whether to retain existing request compatibility or design identifier limits for bounded ticket snapshots. [PR #52](https://github.com/ArborealML/Inference-Router/pull/52) landed the existing body cap on both admin POST routes and corrected SPEC sizing at c6f34c3. All 54 focused tests passed, Linux AOT had zero trim warnings and [main CI](https://github.com/ArborealML/Inference-Router/actions/runs/44793610067) passed all three jobs.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Admission refuses a `model` id or `X-Inference-Thread` value over a stated limit with the one lowercase error shape, naming the limit in the refusal
- [ ] #2 That limit lives in config or a named default, not only in prose, and SPEC states where each identity in the snapshot is bounded and by which mechanism
- [ ] #3 A real HTTP pin sends an over-long model id, asserts the refusal, then asserts a snapshot response body stays under a stated byte ceiling
- [x] #4 The admin POST verbs read their bodies under the same cap as `/v1`: `ServerApp.cs:420` and `:463` call `CopyToAsync` with no limit, while `RouterConfig.cs:164` promises the cap "on any surface"
- [ ] #5 A named pin sets a small body cap, sends a long `X-Inference-Thread` header and a near-cap model, and asserts the serialized snapshot stays under a stated budget
- [ ] #6 The refusal is OpenAI-shaped on `/v1` (`invalid_request`, `param: model`, no reflected value), and the audit of every body consumer decides whether SPEC's "on any surface" survives or becomes "on body-consuming endpoints"
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Prepare a bounded plan to reuse the existing counted body reader on POST /admin/threads and POST /admin/switch. Pin declared and streamed oversized requests returning the existing 413 before registration or switch effects, plus valid requests at the configured ceiling. Correct the SPEC snapshot sizing claim while preserving whole identities. Seek independent review before implementation.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Source on 3fb1981 shows both handlers copy the entire body before parsing. Inference and authorized reset already use the bounded reader; reload accepts no body. A thread header can itself exceed the inference body budget, and JSON escaping also affects the serialized snapshot. Decision 45 retains complete snapshot identities. A new identity limit, fixed snapshot ceiling and refusal fields remain proposals outside this increment.
<!-- SECTION:NOTES:END -->
