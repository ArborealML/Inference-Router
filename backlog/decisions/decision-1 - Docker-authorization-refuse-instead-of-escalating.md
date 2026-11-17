---
id: decision-1
title: 'Docker authorization: refuse instead of escalating'
date: '2026-11-16T20:40:00Z'
status: accepted
---
## Context

The existing selector checks whether the caller can already authorize the daemon and take its
lock files; if not, it re-executes itself under `sudo`, forwarding every flag. That is safe for a
one-shot command. The router is a long-lived process holding queues, admitted requests and switch
state, so re-executing it would discard live state, and a daemon that quietly elevates its own
privileges is harder to audit than one that does not.

## Decision

Options:

1. **Refuse.** The router runs as a user authorized for the daemon socket. When it cannot reach the
   socket it fails preflight/switch with one actionable message and never attempts escalation.
2. **Escalate only in the CLI path.** `arboreal engines switch` may elevate; the daemon may not.
3. **Install-time grant.** Ship a sudoers/polkit snippet scoped to the router binary so elevation is
   explicit in deployment rather than at runtime.

Recommended: 1, with 3 documented as an optional deployment note. 2 is rejected: two privilege
models in one product drift apart, and the scheduler's context-window escalation can never use the
CLI path anyway.

## Consequences

- Requires a one-time operator setup step (group membership or a scoped sudoers rule) and a clear
  error path when it is missing.
- The router never runs as root, so its file-system and socket permissions must be planned: lock
  files and asset receipts need to be writable by the router user.
- The current selector's re-exec behaviour is not ported; whoever switches models by hand keeps that
  tool's behaviour, and the router's behaviour differs on purpose.

- Accepted on the board audit, having sat `proposed` while already implemented: the Docker lifecycle provider refuses and reports one actionable message when it cannot reach the daemon, and nothing in the router re-execs itself under sudo. The one-time operator setup this bullet asks for is still open and is a question for the owner — the daemon-route answer on TASK-20.
