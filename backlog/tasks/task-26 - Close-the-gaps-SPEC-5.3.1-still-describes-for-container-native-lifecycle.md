---
id: TASK-26
title: Close the gaps SPEC 5.3.1 still describes for container-native lifecycle
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T02:54:00Z'
updated_date: '2026-11-17T02:54:00Z'
labels:
  - engine
  - lifecycle
  - security
dependencies: []
priority: medium
ordinal: 26000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
TASK-1 put the seam and the first Docker provider into main; §5.3.1 describes more than that, and the difference is now written into the spec rather than left to guesswork. What is missing is not cosmetic: presence-only image checks mean a moving tag is not the pin the design promises; no GPU or mount configuration means a real inference image cannot be started as configured; no exec, logs or daemon-event subscription means the router can start a container but cannot ask it anything after; latched failures are what stop a dispatcher wake-up from turning one broken loader into a retry loop.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 An image is verified by identity (Id, RepoDigests, platform), not by the tag resolving at all, and a mismatch refuses the switch with one actionable message
- [ ] #2 GPU device requests, mounts and resource limits are expressible in config and reach the create request
- [ ] #3 Exec frame demux and log reading exist behind the provider seam with a fake, and a non-zero exec exit is a failure
- [ ] #4 A failed switch is latched: a dispatcher wake-up cannot retry it, and the latch is cleared by an explicit operator action or a config change
- [ ] #5 Daemon event subscription (or a documented polling substitute) replaces the assumption that nothing else changed the engine
- [ ] #6 The Windows named-pipe transport is implemented or explicitly declined in SPEC with the reason
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Also waiting on the daemon-route answer in TASK-20 for end-to-end verification against a real Docker host; everything else is testable against the fake daemon handler.
<!-- SECTION:NOTES:END -->
