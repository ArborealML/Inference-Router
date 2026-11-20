---
id: TASK-97
title: >-
  Stop the admin ticket snapshot echoing caller identifiers at a length nothing
  bounds
status: Preparing
assignee:
  - '@gpt-6.2-sol-stream-frames'
created_date: '2026-11-18T10:57:00Z'
updated_date: '2026-11-20T17:31:00Z'
labels: []
milestone: m-1
dependencies: []
ordinal: 96000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Frames is checking current admission, admin snapshot and body-reader behavior against these criteria and their proposed limits. Identify any reachable issue and distinguish existing protections from proposed changes to accepted identities. The [keepalive frame change](https://github.com/ArborealML/Inference-Router/pull/50) bounds displayed status only.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria

<!-- AC:BEGIN -->
- [ ] #1 Admission refuses a `model` id or `X-Inference-Thread` value over a stated limit with the one lowercase error shape, naming the limit in the refusal
- [ ] #2 That limit lives in config or a named default, not only in prose, and SPEC states where each identity in the snapshot is bounded and by which mechanism
- [ ] #3 A real HTTP pin sends an over-long model id, asserts the refusal, then asserts a snapshot response body stays under a stated byte ceiling
- [ ] #4 The admin POST verbs read their bodies under the same cap as `/v1`: `ServerApp.cs:420` and `:463` call `CopyToAsync` with no limit, while `RouterConfig.cs:164` promises the cap "on any surface"
- [ ] #5 A named pin sets a small body cap, sends a long `X-Inference-Thread` header and a near-cap model, and asserts the serialized snapshot stays under a stated budget
- [ ] #6 The refusal is OpenAI-shaped on `/v1` (`invalid_request`, `param: model`, no reflected value), and the audit of every body consumer decides whether SPEC's "on any surface" survives or becomes "on body-consuming endpoints"
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The top-tier plan review landed REVISE with numbers, adopted here as **provisional**: a named `MaxStoredIdentifierUtf8Bytes = 2048` enforced on the decoded `model` and `X-Inference-Thread` before any ticket or thread state is mutated, and a `MaxAdminTicketSnapshotJsonBytes = 65536` checked before emission with a lossless round trip of every field — never a truncated or routinely-failing snapshot. Admin POSTs reuse `/v1`'s bounded reader on actual streamed bytes, rejecting cap+1 before side effects. Both a domain check on the header and explicit transport header limits. Retention stays 4096; two 2048-byte identities across it is 16 MiB of raw identifier bytes, so no new eviction rule. One SPEC+code+tests commit.
This seat's own compatibility check, which the reviewer could not run: the long-identifier fixtures are 405 (`CapabilityPublicationTests.cs:1033`, `EventRingTests.cs:261-262,351-352`) and 300 characters, all far under 2048, so no existing pin breaks; SPEC §6 carries no bar on a new admission refusal. The reviewer read no local tree — it could not see SPEC §§6/11.2, all routes, or the full DTO — so those audits are criteria here, not settled facts. Wake satisfied; implementation waits behind TASK-94 and TASK-87.
<!-- SECTION:NOTES:END -->
