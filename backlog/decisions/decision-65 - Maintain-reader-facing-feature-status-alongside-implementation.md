---
id: decision-65
title: Maintain reader-facing feature status alongside implementation
date: '2026-11-18T19:58:00Z'
status: accepted
---
## Context

Readers use the README to decide whether AIR can serve their workload. The backlog and SPEC include both implemented behavior and future work.

## Decision

Maintain a concise Features section in the README. Describe capabilities available on main and keep separate subsections for work in progress and planned features. The implementer updates the affected entry with the behavior change. The coordinator checks delivery status and limitations at integration.

## Consequences

The README can describe an available subset of a feature while tracking its remaining work. Link to the delivery milestones for dependencies and detailed acceptance criteria.
