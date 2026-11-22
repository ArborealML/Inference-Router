---
id: m-14
title: "Hardware-specific inference tuning"
---

## Description

Design and deliver a repeatable per-engine, per-model tuning workflow. Compare measured stable concurrency and speed across supported context windows, preserving hardware and effective runtime identity across upgrades.

## Delivery order

The offline planner and comparison commands are shipped. Live execution, candidate search and the resumable catalog sweep follow in order.

1. [Run a repeatable live tuning workload for one engine and model](../tasks/task-170%20-%20Run-a-repeatable-live-tuning-workload-for-one-engine-and-model.md)
2. [Search bounded configurations and recommend a measured winner for one model](../tasks/task-171%20-%20Search-bounded-configurations-and-recommend-a-measured-winner-for-one-model.md)
3. [Sweep the configured catalog with air tune run --all-models and --resume](../tasks/task-172%20-%20Sweep-the-configured-catalog-with-air-tune-run-all-models-and-resume.md)
