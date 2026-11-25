---
id: TASK-189
title: Stop the router relaunching an engine onto the tier it is already serving
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-25T18:00:00Z'
updated_date: '2026-11-25T18:38:00Z'
labels: []
dependencies: []
priority: high
ordinal: 181000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The live event feed records a switch to the profile 256k-8 at 17:40 on an engine that was already serving 256k-8 with a measured window of 262144 and 8 slots. The old container exited at that moment and a new one started; the switch took about two minutes, and every request in flight on the only healthy engine died with it. The same thing happened at 06:00 on this host, and at startup the router launched the model at the narrow-slot default tier and then immediately switched it to the wide-slot tier of the same window. A no-op target must be recognised before any lifecycle transaction begins.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A demand already satisfied by the resident tier never starts a switch.
- [ ] #2 A pin reddens when a switch names the tier the engine is already serving.
- [ ] #3 Startup does not launch the default tier and then immediately switch away from it when a wider tier of the same window satisfies demand.
- [ ] #4 Any switch that does start records the demand that required it in the feed with the target and the resident state.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Confirmed live 2026-11-25: switch to the resident tier at 17:40:18 (container exit 17:40:19, completed after 115.8s) and at 06:00 (118.0s); at startup the default tier was launched then immediately switched. Fix sequence agreed with the transition design: first make every lifecycle stop check for in-flight work and refuse a busy resize, then guard the no-op case so a demand the resident tier already satisfies never starts a transaction, then report the demand that forced a legitimate one.
<!-- SECTION:NOTES:END -->
