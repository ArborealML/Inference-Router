---
id: decision-73
title: Release implementers at local qualification
date: '2026-11-20'
status: accepted
---

## Decision

Delivery identifies the next ranked independent assignment before the current item finishes focused qualification. After a local pass and exact candidate handoff, the implementer starts the next eligible item in its own worktree. A pending hosted check, independent commit review or merge gates that candidate’s landing and leaves the worker available. A pending plan review gates implementation of that plan only.

Delivery monitors the candidate through landing, returning actionable failures to its original author at a safe work boundary. Preserve the tested commit and evidence for corrections. Report the actual blocker when no ranked assignment can advance. Existing independent review, acceptance, concurrency and host authorization limits remain in effect.
