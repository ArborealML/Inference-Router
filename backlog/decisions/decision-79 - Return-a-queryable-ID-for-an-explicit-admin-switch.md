---
id: decision-79
title: Return a queryable ID for an explicit admin switch
date: '2026-11-20T21:29:00Z'
status: accepted
---
## Context

Queue demand normally drives model switches. An operator can also request a switch explicitly. Today the admin endpoint can acknowledge that request before the load finishes, leaving the caller without a way to retrieve its outcome.

## Decision

Keep queue-driven switching as the normal path. When AIR accepts an explicit admin switch request, acknowledge it asynchronously and return an operation ID. Let the caller query that ID for the status and eventual outcome of that request, including the reason for failure.

## Consequences

TASK-104 owns the admin request and lookup contract. Its implementation plan will specify the response and lookup shapes, correlation through completion, and tests for success and failure. Queue-driven switching continues to operate without an admin request.
