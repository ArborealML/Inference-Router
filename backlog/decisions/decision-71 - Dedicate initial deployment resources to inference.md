---
id: decision-71
title: Dedicate initial deployment resources to inference
date: '2026-11-19'
status: accepted
---
## Decision

For the initial version, assume the system's resources are dedicated to inference. Do not design the first managed swap around sharing capacity with unrelated workloads. Decision 68 continues to cover the installed inference engines and selector configurations on the primary host.

## Implementation interpretation

SPEC §5.3 assigns a router-managed engine one lifecycle owner. Ordinary tools that start or stop its serving model must coordinate with that owner, so a second launcher cannot replace a model while AIR is serving it. The coordinator may proceed with offline implementation on that basis. The choice of a broker is an engineering proposal. The dedication assumption establishes neither verified host enforcement nor approval to alter current host permissions or interrupt the resident.
