---
id: m-17
title: "Independent provider progress"
---

## Description

An eligible request reaches its provider while another domain is full, switching, draining or rate-limited. Verify both directions for same-model and different-model work, including callers with or without a thread ID, finite deadlines, cancellation, refusal, wake races and intentionally shared caps. Probing eligibility must leave admission and physical state unchanged. Preserve original priority and enqueue order among actual competitors. Account backoff evidence comes from commercial routing; bounded admission and measured fairness retain their own task owners.
