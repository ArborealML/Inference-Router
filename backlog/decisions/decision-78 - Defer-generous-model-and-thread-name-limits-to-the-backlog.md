---
id: decision-78
title: Defer generous model and thread name limits to the backlog
date: '2026-11-20T18:40:00Z'
status: accepted
---
## Context

Admin ticket snapshots expose complete model and thread names for exact correlation. The request-body limit does not bound a separately supplied thread header or the size of escaped JSON.

## Decision

A future admission limit for model and thread names is acceptable. Keep it generous. The owner suggested 128–256 characters as a guide and assigned the work low priority.

## Consequences

Keep the implementation on the backlog. When scheduled, specify the exact limit and how characters are counted, check compatibility, and verify the resulting serialized snapshot bounds. Decision 45 continues to apply to accepted names.
