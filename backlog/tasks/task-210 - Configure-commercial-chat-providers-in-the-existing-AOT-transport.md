---
id: TASK-210
title: Configure commercial chat providers in the existing AOT transport
status: In Progress
assignee:
  - '@gpt-6.2-sol-config'
created_date: '2026-11-27'
updated_date: '2026-11-28T14:18:17Z'
labels: []
milestone: m-20
dependencies:
  - TASK-187
priority: high
ordinal: 210000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Implement configuration-defined direct OpenAI Chat Completions providers using the existing HTTP relay and semantic observer. Start with Isoquant and DeepRelay. Use controlled direct transport to qualify identity and usage. Live metered inference requires the separately verified spend guard.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Two disabled-by-default example provider definitions load through the same AOT binary with configured origin, credential reference, model mapping, capabilities, price and cap.
- [ ] #2 Explicit model equivalence and required parameter semantics are checked; backup substitution is disabled where required and actual served identity is verified.
- [ ] #3 Standard usage and errors need no mappings; typed header/JSON scalar, units, timestamp and finite code overrides cover demonstrated cases only.
- [ ] #4 Credentials stay outside definitions/logs, redirects disabled by default, outbound headers allowlisted; validated reload freezes admitted configuration and revalidates revocations.
- [ ] #5 Observe terminal usage, actual model, error versus sentinel and first semantic output separately; byte relay and legacy completions remain compatible.
- [ ] #6 Tests cover malformed mapping, missing/invalid/zero, overflow, bounds and same-binary smoke; SPEC §§5–7, 9–10, 14 and README distinguish implemented from planned.
- [ ] #7 Transport classifies every owned-policy/hybrid destination, including legacy remote and count/tokenize; refuse unknown trust. Commercial sends require bound authorization; private sends require positive trust and current policy. Capture fixtures use fake permits.
- [ ] #8 Missing or wrong authoritative identity withholds semantic output. A later identity conflict ends further output, records partial failure, retains liability and forbids replay. Unsupported protocols refuse strict routing.
- [ ] #9 Controlled clock bounds first meaningful output and strict identity verification even with keepalives; wrong/unknown identity refuses before semantic relay and retains unsettled exposure.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Extend the existing source-generated engine configuration, semantic observer and HTTP relay. Qualify two disabled provider definitions with explicit origin, model equivalence, capabilities, credential reference, price and limits. Validate startup and authenticated reload in TOML and JSON. Keep disabled definitions inert under rollback to older parsers. Verify zero credential access or traffic at startup, polling, discovery, health, token counting and reload. Keep topology changes router-idle and recheck revocation for uncommitted sends.

Construct the direct client with redirects, ambient proxy and cookies disabled. Freeze method, origin, provider/account, credential revision and body. Reject failed projection or duplicate protected parameters. Exclude private thread/cache metadata. Use the existing transport only with an exact one-use permit; tests can supply a controlled permit. Keep production metered sending disabled until the durable spend guard lands. Cover alternate authorities, auxiliary requests and revoked configurations.

Extend the existing SSE observer to verify the served model before releasing semantic output. Bound held bytes and the elapsed time until identity and first output, including a stream of keepalives. Validate buffered responses and streaming batches, including reasoning and tool output, against bounded identity, usage and error rules before relay. A late conflict preserves earlier delivery and forbids replay. Verify exact downstream bytes and legacy behavior through ordinary HTTP with loopback capture. Resolve configuration differences through finite typed mappings. Actual account access and spend authorization belong to release qualification.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Disabled providers are inert. Controlled fixtures cover strict declarations, projection and served identity. TASK-211–213 own account controls.

[Controlled direct transport](https://github.com/ArborealML/Inference-Router/pull/105) passed 180 source checks and a two-provider Native AOT loopback run. Integrated Release qualification passed 159 tests. Review at d045d69 found two configured error categories collapsed, no finite stream bound after first output, and partial terminal frames exceeding the configured tail limit. Correct all three and qualify physical slot release with a waiting successor. Live provider qualification and spending authority remain open.
<!-- SECTION:NOTES:END -->
