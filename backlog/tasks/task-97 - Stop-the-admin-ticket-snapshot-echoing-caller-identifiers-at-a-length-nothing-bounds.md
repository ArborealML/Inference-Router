---
id: TASK-97
title: >-
  Stop the admin ticket snapshot echoing caller identifiers at a length nothing
  bounds
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T10:57:00Z'
labels: []
dependencies: []
ordinal: 96000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
SPEC §10 credits `max_request_body_bytes` with bounding the admin ticket snapshot, and that sentence is doing work it cannot do. Measured: the default is 16 MiB (`RouterConfig.cs:167`) and no sample overrides it, so the whole-identity echo the snapshot returns is bounded by a limit chosen for *request bodies* — about five orders of magnitude above the 128-character reporting caps this feature exists to enforce — and the response itself is a GET with no request body at all. `ThreadId` is not body-supplied either: it arrives on the `X-Inference-Thread` header (`ServerApp.cs:109`) and no `MaxRequestHeaders*` limit is configured in the server, so its ceiling is a transport default the repository never names. The row asks for a bound that was actually chosen: at admission, on what may be stored.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria

<!-- AC:BEGIN -->
- [ ] #1 Admission refuses a `model` id or `X-Inference-Thread` value over a stated limit with the one lowercase error shape, naming the limit in the refusal
- [ ] #2 That limit lives in config or a named default, not only in prose, and SPEC states where each identity in the snapshot is bounded and by which mechanism
- [ ] #3 A real HTTP pin sends an over-long model id, asserts the refusal, then asserts a snapshot response body stays under a stated byte ceiling
- [ ] #4 The admin POST verbs read their bodies under the same cap as `/v1`: `ServerApp.cs:420` and `:463` call `CopyToAsync` with no limit, while `RouterConfig.cs:164` promises the cap "on any surface"
- [ ] #5 A named pin sets a small body cap, sends a long `X-Inference-Thread` header and a near-cap model, and asserts the serialized snapshot stays under a stated budget
- [ ] #6 The plan is reviewed at the top reasoning tier before code, because refusing what `/v1` used to accept is a wire-contract change
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Deliverable, decided after the coding seat measured the default and this seat re-measured it (`RouterConfig.cs:167`, `grep request_body samples/` → no override): bound what may be **stored**, do not cut what is read. A cut `Model` looks valid and matches nothing, which is why v0.12.40 rejected the `Ident`-on-`Model` shape and pinned whole values; decision-45 does not decide this surface either. A general admin response-size rule is **not** this row — SPEC calls it a separate question, and if that is the preferred lever it belongs with the admin/live-view surface (TASK-21). Verified by read: `ServerApp.cs:109` header path; no `MaxRequestHeaders*` in the server assembly; `GET /admin/tickets/{id}` carries no body.
The deep commit review of 8e6867d reached the same place from the other side, and this seat confirmed the path in this tree: `CopyToAsync` at `ServerApp.cs:420` and `:463` with no limit, and ticket retention of 4,096 (`RouterCore.cs:28`), so one stored oversized identity is re-fetchable thousands of times. Kestrel's own defaults (32 KiB of headers, 30 MB bodies) are neither the router's cap nor the one SPEC names.
<!-- SECTION:NOTES:END -->
