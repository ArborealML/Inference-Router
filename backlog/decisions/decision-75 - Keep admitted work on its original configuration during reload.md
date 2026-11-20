---
id: decision-75
title: Keep admitted work on its original configuration during reload
date: '2026-11-20'
status: accepted
---

## Decision

Apply validated default changes to new work. Active streams and claimed switches retain their original configuration generation. For the first release, external engine additions and removals require router-wide idle. Check idle and exclude new admission atomically. Detached inference remains physical work until cleanup confirms retirement. A busy or invalid apply preserves the current generation. This narrows the startup-only assumption in decision 50. Selective native quarantine and reset remain open.
