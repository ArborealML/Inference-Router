---
id: decision-77
title: Opt in to exact native target reset with the reload credential
date: '2026-11-20'
status: accepted
---

## Decision

A file-backed server may reuse its startup reload Bearer credential for an operator reset when the operator also enables reset explicitly at startup. Supplying the reload token alone continues to enable only configuration reload across upgrades.

The reset applies to one engine, canonical model and effective profile. Check the current process and configuration generation from the authenticated ETag when changing the latch. A stale or foreign precondition leaves every latch intact. Success clears only the matching scheduler failure latch and permits a later independently authorized attempt. Uncertain effects, host ownership, readiness and dispatch retain their existing checks. Reset itself issues no lifecycle operation.
