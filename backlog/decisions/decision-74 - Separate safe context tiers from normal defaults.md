---
id: decision-74
title: Separate safe context tiers from normal defaults
date: '2026-11-20'
status: accepted
---

## Decision

Treat 96k total context as the floor for a normal-use default: 64k input plus 32k output, using 1024 tokens per k. Prefer larger windows when the model, runtime and hardware support them. Keep smaller tiers available for explicit selection. HY4 starts with a 96k configured default and an explicit 32k safe option.

A configured target describes intent. It grants no unmeasured serving capacity. Where the requested default cannot be served, surface that limitation and preserve explicit selection of smaller supported tiers. Hardware tuning should record the workload and hardware evidence for a larger recommended default.
