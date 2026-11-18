---
id: TASK-89
title: Configure pi to use the router, so /model offers its models
status: Waiting
assignee:
  - '@qwen3.8-flash-next-board'
created_date: '2026-11-18T07:15:00Z'
updated_date: '2026-11-18T19:23:00Z'
labels:
  - serve
  - pi
milestone: m-0
dependencies: []
priority: high
ordinal: 88000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wake when the router supplies verified model windows for the pi picker. Two real turns through an isolated router and pi profile have qualified the two-model client path.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 pi names the router's loopback endpoint and the models it offers, in a config file a reader can find.
- [ ] #2 Each offered model declares its context window, so a /model choice is honest about what it buys.
- [x] #3 No change moves the pi lane an agent session is running on; that move waits for a named window and the owner's nod.
- [x] #4 Two verified models appear in /model and a real pi turn completes on each through the router.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Read back the current pi provider without printing secrets. Exercise /model and complete a real turn on the verified resident model in an isolated pi session. Record the model, window and client outcome. Repeat for the second model only after availability and authorization are established.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
On 2026-11-18 pi 1.3.0 selected the resident model through /model and completed one real Chat turn on the isolated Release router at 5c5e18a. The selected provider and model matched the session record, stopReason was stop, and exactly one completion returned HTTP 200. The private test profile used a separate listener and the shared provider file was byte-identical afterwards. The configured 131072-token value still lacks a live window observation. The deployed router and second model remain unqualified.

At 5c5e18a, one isolated pi 1.3.0 session selected two models through /model on one AIR listener. Local Qwen completed the marker turn; remote resident Ternary Bonsai answered 5 and finished with stop. The Dax backend reported 8192 context and one slot. A provider wildcard hid the slash-containing Bonsai ID; removing the optional CLI filter exposed it. The two engines were already serving and the production listener was untouched. TASK-112 still requires an observed native residency transition.
<!-- SECTION:NOTES:END -->
