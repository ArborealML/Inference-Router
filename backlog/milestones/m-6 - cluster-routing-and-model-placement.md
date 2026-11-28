---
id: m-6
title: "Cluster routing and model placement"
---

## Description

Submit through either ingress to one authoritative router and its queue. A managed peer loads the model under its own physical authority. First qualify static configured placement, independently progressing resource domains, and refusal/partition/restart/ambiguous acceptance. Do not wait for measured placement or autonomous tuning. SPEC §12.

The owner's highest feature priority is a two-host deployment with one client-facing AIR endpoint. The first useful run keeps DeepSeek resident on the primary host while a tailnet peer with one RTX 3090 and 64 GiB RAM serves Qwen 27B and Flash Next in turn. Verify each model fits independently on that peer before selecting its serving profile.

For acceptance, keep a DeepSeek stream active, send ordinary Qwen requests through the same ingress, observe both Qwen models respond from the peer across a model switch, and verify the primary DeepSeek process stays resident. Express host preferences through configuration so other hosts and models can use the same path. The peer owns local activation and reports observed readiness separately from its catalog. Make the fallback policy explicit when the peer is unavailable.

TASK-203/204 establish independent dispatch and identity. TASK-206 provides the in-process manager/issuer seam before production TASK-25/118/110; TASK-190/195/197/198 establish exact transition/recipe and recovery contracts, TASK-77 adds equivalent authenticated peer roles. TASK-122 builds fixed placement/forwarding; TASK-124 qualifies actual simultaneous streams, both Qwen models on the peer and retained primary DeepSeek, refusal/restart and explicit fallback. TASK-51 supplies finite default expiry before live rollout. TASK-219 provenance diagnosis is parallel, not a mandatory reproduction gate for a clean new build. TASK-123 offline measured scoring may overlap; enable adaptive placement after fixed placement and TASK-72 fairness are qualified. Existing forwarding and same-host evidence do not establish managed cluster qualification.

External infrastructure supplies exclusive host resource access, credential protection and cutover/rollback. AIR manages inference within that boundary, not general host provisioning or sandboxing.
