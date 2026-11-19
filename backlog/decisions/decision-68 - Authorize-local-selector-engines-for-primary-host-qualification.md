---
id: decision-68
title: Authorize local selector engines for primary-host qualification
date: '2026-11-19'
status: accepted
---
## Decision

The owner authorizes running any inference engine configured by the primary host's installed model selector. The local router configuration must support every valid selector permutation. Qualify a real two-model swap on this host first, then extend to peer hosts and their verified models.

## Consequences

This supersedes the earlier live-test parking for these local engines. Record actual residency, inference, configuration and lifecycle effects. The existing ownership and drain requirements still apply to router-managed transitions.
