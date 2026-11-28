---
id: m-6
title: "Cluster routing and model placement"
---

## Description

Submit through either ingress to one authoritative router and its queue. A managed peer loads the model under its own physical authority. First qualify static configured placement, independently progressing resource domains, and refusal/partition/restart/ambiguous acceptance. Do not wait for measured placement or autonomous tuning. SPEC §12.

The owner's highest feature priority is a two-host deployment with one client-facing AIR endpoint. The first useful run keeps DeepSeek resident on the primary host while a tailnet peer with one RTX 3090 and 64 GiB RAM serves Qwen 27B and Flash Next in turn. Verify each model fits independently on that peer before selecting its serving profile.

For acceptance, keep a DeepSeek stream active, send ordinary Qwen requests through the same ingress, observe both Qwen models respond from the peer across a model switch, and verify the primary DeepSeek process stays resident. Express host preferences through configuration so other hosts and models can use the same path. The peer owns local activation and reports observed readiness separately from its catalog. Make the fallback policy explicit when the peer is unavailable.

TASK-203/204 establish independent scheduling; TASK-110/118/190/197/198 establish safe physical ownership and conformance; TASK-206 extracts the equivalent in-process/remote manager seam; TASK-77 finishes remote authority; TASK-122 and TASK-124 qualify the static pair, recovery and either ingress. TASK-123 adds measured placement afterward. Existing resident forwarding and same-host evidence are retained but are not managed cluster qualification.
