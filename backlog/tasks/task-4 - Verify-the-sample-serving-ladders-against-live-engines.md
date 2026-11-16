---
id: TASK-4
title: Verify the sample serving ladders against live engines
status: Done
assignee: []
created_date: '2026-11-16T20:40:00Z'
labels: []
dependencies: []
ordinal: 4000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The shipped sample configs declare context/slot ladders that were written from expectation, not measurement. Probe a live engine per backend and correct the samples, or annotate which values are unverified.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Each ladder entry in samples/router.toml has a measured context and slot count or is marked unverified
<!-- AC:END -->

## Result (verified 2026-11-16)

Probed the live engine on the reference box (key read from the container's own key file, never
written into the repo): `/v1/models` reports `meta.n_ctx = 524288` for the loaded model and
`/props` reports `total_slots = 1`, input modalities text+image.

`samples/router.toml` now carries a `512k-1` tier with those measured numbers, the probe command
is written into the sample, and the three remaining tiers (98304/4, 131072/2, 1048576/1) plus the
engine `capabilities` block are explicitly marked UNVERIFIED with the probe they need. The
tier *name* still has to be reconciled with the selector's profile list — noted in the sample.
`arboreal config` dry run loads the sample; `ConfigLoaderTests.Shipped_Sample_Configs_Load_As_Written`
pins the measured tier (66/66 green).
