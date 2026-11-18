---
id: decision-59
title: Track testing and coordinator handoff as explicit task stages
date: '2026-11-18T18:29:00Z'
status: accepted
---
## Context

The board needs to show when an agent is verifying its change and when a coordinator can take it for review.

## Decision

Display the columns in this order: To Do → Blocked → Waiting → Preparing → In Progress → Testing → Ready for Review → Done. The assigned agent owns preparation, implementation, verification and the handoff. It initiates each status update as the work changes. The manager records updates in the shared board. When the agent delivers the tested diff or commit, check results and any remaining limitations, the coordinator becomes the next responsible actor.

## Consequences

A failed test needing a fix returns the task to In Progress. An agent waiting for a test slot records the wake condition in Waiting. Coordinator-requested revisions return to the implementing agent. Both new statuses count as open.

The coordinator plans and reviews with ChatSwarm when available. Current subagents finish their assignments individually before a dedicated, long-lived tmux implementer succeeds each one. Implementers work in isolated task worktrees; the coordinator integrates their handoffs and retires completed worktrees after preserving evidence. The tmux session stays available for subsequent assignments.
