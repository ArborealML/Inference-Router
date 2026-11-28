---
id: m-14
title: "Hardware-specific inference tuning"
---

## Description

Design and deliver a repeatable per-engine, per-model tuning workflow. Compare measured stable concurrency and speed across supported context windows, preserving hardware and effective runtime identity across upgrades.

## Delivery order

The offline planner and comparison commands are shipped. Live execution, progressive candidate search, real matched-work comparison and explicit adoption follow. The resumable sweep follows search but is not a prerequisite to using a qualified operating point.

1. [Run a repeatable live tuning workload for one engine and model](../tasks/task-170%20-%20Run-a-repeatable-live-tuning-workload-for-one-engine-and-model.md)
2. [Search bounded configurations and recommend a measured winner for one model](../tasks/task-171%20-%20Search-bounded-configurations-and-recommend-a-measured-winner-for-one-model.md)
3. [Qualify real matched-work measurements](../tasks/task-154%20-%20Measure-live-model-profiles-and-recommend-a-configuration-for-the-host.md) and [explicit adoption](../tasks/task-216%20-%20Adopt-qualified-local-operating-points-explicitly.md)
4. [Sweep the configured catalog with air tune run --all-models and --resume](../tasks/task-172%20-%20Sweep-the-configured-catalog-with-air-tune-run-all-models-and-resume.md)

The ladder is 64K, 96K, 128K, 196K, 256K, 512K, 768K, 1024K (binary K); qualify supported tiers only. Each next tier is capped at the preceding measured optimum, with explicit exhaustive override. Keep physical launch lanes, observed occupancy and admission targets distinct. Use identical workloads across recipes for runtime comparison, preserve failures and untested points, and adopt only verified evidence. TASK-192 consumes it; no comprehensive catalog benchmark is required for first delivery.
