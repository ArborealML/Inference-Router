---
id: TASK-26
title: Close the gaps SPEC 5.3.1 still describes for container-native lifecycle
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T02:54:00Z'
updated_date: '2026-11-17T07:54:00Z'
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
- [ ] #7 A switch whose start call fails leaves no container behind: the provider reinspects the id it was given and removes what its own labels claim, instead of orphaning a dead container on the host
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Also waiting on the daemon-route answer in TASK-20 for end-to-end verification against a real Docker host; everything else is testable against the fake daemon handler.

External review of the Docker provider landed in 18d7279: named interface for published ports (host_ip, loopback default), router_id required, stopped containers removed (remove_stopped), stop accepts 304/404/409, start accepts 304 only (the generic 404-for-POST swallow used to report a launch that never happened), malformed 200 list is a failed ownership query, list filter serialised through the source-generated context, ids URL-escaped, proxy/redirect disabled, bare socket path treated as a socket. What that review found and is STILL open here: an orphan container left by a create that succeeded and a start that failed (no reinspect, no remove, and the labels make it ours to clean up); image identity pinning (presence only, no Id/RepoDigests/platform compare); mounts/GPU/resource limits; exec frame demux, logs, daemon event subscription; npipe; the latched-failure rule. Per-engine (not per-target) single-flight is now stated in SPEC 5.3.1 rather than left implied.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T07:54:00Z

---

Route question (TASK-20) is answered: unix socket, daemon reachable by this account, no TLS endpoint. All seven criteria here stay buildable against the fake daemon handler, so this task is not blocked by anything. What it cannot do is *show* a lifecycle transaction against a live daemon; that permission is now TASK-33 and it gates only the demonstration, not the implementation. Recorded so nobody re-derives the route question.

---
<!-- COMMENTS:END -->
