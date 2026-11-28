---
id: m-14
title: "Hardware-specific inference tuning"
---

## Description

Design and deliver a repeatable per-engine, per-model tuning workflow. Compare measured stable concurrency and speed across supported context windows, preserving hardware and effective runtime identity across upgrades.

## Delivery order

The offline planner, comparison commands and [single-model workload runner](https://github.com/ArborealML/Inference-Router/pull/98) have merged. The runner uses controlled measurement and admission providers in qualification. Running it on stock hardware requires a production provider that can reserve the device and verify measurement and release. Progressive candidate search remains open. Real matched-work comparison and explicit adoption are production evidence gates, not prerequisites to implementing the search/adoption mechanisms. The resumable sweep follows search but is not a prerequisite to using a qualified operating point.

1. [Run a repeatable live tuning workload for one engine and model](../tasks/task-170%20-%20Run-a-repeatable-live-tuning-workload-for-one-engine-and-model.md)
2. [Search bounded configurations and recommend a measured winner for one model](../tasks/task-171%20-%20Search-bounded-configurations-and-recommend-a-measured-winner-for-one-model.md)
3. [Qualify real matched-work measurements](../tasks/task-154%20-%20Measure-live-model-profiles-and-recommend-a-configuration-for-the-host.md) and [explicit adoption](../tasks/task-216%20-%20Adopt-qualified-local-operating-points-explicitly.md)
4. [Sweep the configured catalog with air tune run --all-models and --resume](../tasks/task-172%20-%20Sweep-the-configured-catalog-with-air-tune-run-all-models-and-resume.md)

The ladder is 64K, 96K, 128K, 196K, 256K, 512K, 768K, 1024K (binary K); qualify supported tiers only. Each next tier is capped at the preceding measured optimum, with explicit exhaustive override. Keep physical launch lanes, observed occupancy and admission targets distinct. Use identical workloads across recipes for runtime comparison, preserve failures and untested points, and adopt only verified evidence. TASK-192 consumes it; no comprehensive catalog benchmark is required for first delivery.

TASK-170/171 implement and fixture-qualify runner/search before live trials. TASK-154 owns the authorized real reference (Hy4 where available), matched-work measurements and representative real mixed/staggered replays for any enabled adaptive mode. TASK-216 implements validation/adoption from the evidence interface, independently of actual trial permission; only explicit adoption of real compatible evidence hashes for each enabled workload/mutation mode grants production capacity. TASK-191's startup regression does not depend on the optimizer. TASK-192's synthetic evaluator can land disabled; real qualified evidence and host authority gate physical changes. Keep per-domain drain/outage budgets and durable failed-attempt backoff.
