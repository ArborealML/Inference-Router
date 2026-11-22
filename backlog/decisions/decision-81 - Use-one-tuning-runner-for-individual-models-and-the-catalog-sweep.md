---
id: decision-81
title: Use one tuning runner for individual models and the catalog sweep
date: '2026-11-22T09:22:00Z'
status: accepted
---

## Context

The offline planner and comparison commands already exist. Live tuning needs both a targeted run for a single model and repeatable coverage of the configured catalog.

## Decision

Implement one live runner for a configured engine/model pair. Use that runner for bounded single-model candidate search. Add `--all-models` to orchestrate the same search over canonical pairs and `--resume` to reuse compatible completed measurements.

## Consequences

Both modes share residency verification, workload generation, measurement and scoring. Publish recommendations against stated workload requirements and preserve existing defaults until a recommendation is explicitly applied. Tasks 170 through 172 deliver the three stages in order.
