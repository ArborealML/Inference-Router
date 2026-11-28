---
id: TASK-210
title: "Configure commercial chat providers in the existing AOT transport"
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-27'
labels: []
milestone: m-16
dependencies:
  - TASK-187
priority: high
ordinal: 210000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Extend HTTP forwarding and the existing model rewrite/stream observer with configuration-defined OpenAI Chat Completions providers. Start with Isoquant and DeepRelay; no OpenRouter intermediary or vendor-specific compiled adapters.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Two disabled-by-default example provider definitions load through the same AOT binary with configured origin, credential reference, model mapping, capabilities, price and cap.
- [ ] #2 Explicit model equivalence and required parameter semantics are checked; backup substitution is disabled where required and actual served identity is verified.
- [ ] #3 Standard usage and errors need no mappings; typed header/JSON scalar, units, timestamp and finite code overrides cover demonstrated cases only.
- [ ] #4 Credentials stay outside definitions/logs, redirects disabled by default, outbound headers allowlisted; validated reload freezes admitted configuration and revalidates revocations.
- [ ] #5 Observe terminal usage, actual model, error versus sentinel and first semantic output separately; byte relay and legacy completions remain compatible.
- [ ] #6 Tests cover malformed mapping, missing/invalid/zero, overflow, bounds and same-binary smoke; SPEC §§5–7, 9–10, 14 and README distinguish implemented from planned.
- [ ] #7 Transport exposes a common gate for hybrid sends, including legacy remote and count/tokenize; without bound authorization refuse before bytes. Capture fixtures use fake permits, not live credentials.
- [ ] #8 Strict model identity is established before semantic output or the route is refused; bounded observer/buffer only where the protocol supports it.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Reuse TOML/source-generated DTOs, model projection and HTTP/SSE observer. Compile protocol semantics, not vendor code. Fixture-only definitions and legacy migration remain disabled for commercial egress until TASK-212's integrated guard is qualified. No redirect or unauthorized proxy/origin, no caller-controlled credentials; sanitize upstream errors. Poll approved telemetry with bounds outside dispatch. Keep decision-75 topology edits router-idle; revocation of uncommitted sends is separate. No vendor DSL or paid calls.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Detailed account controls belong to TASK-211–213; other protocol integrations are deferred.
<!-- SECTION:NOTES:END -->
