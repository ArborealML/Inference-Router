---
id: TASK-97
title: >-
  Stop the admin ticket snapshot echoing caller identifiers at a length nothing
  bounds
status: To Do
assignee:
  - '@gpt-6.2-sol-stream-delivery'
created_date: '2026-11-18T10:57:00Z'
updated_date: '2026-11-20T18:40:00Z'
labels: []
milestone: m-1
dependencies:
  - TASK-145
priority: low
ordinal: 96000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Low-priority backlog under decision 78. A future generous limit for model and thread names is approved in principle. The owner suggested 128–256 characters. [PR #52](https://github.com/ArborealML/Inference-Router/pull/52) shipped the two admin request-body limits separately and passed all 54 focused tests and [main CI](https://github.com/ArborealML/Inference-Router/actions/runs/44793610067).
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
When prioritized, define the exact character count and unit, assess client compatibility, and derive a serialized snapshot byte bound including escaping and other identity fields. Review before implementation.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Decision 45 preserves the whole accepted identifier for exact correlation. A thread header is independent of the body cap. The shipped admin route checks remain covered by criterion 4.
<!-- SECTION:NOTES:END -->
