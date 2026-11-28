---
id: TASK-199
title: Add a 1M-token single-lane serving tier for Flash Next on the router host
status: Blocked
assignee:
  - '@claire'
created_date: '2026-11-26T09:57:00Z'
updated_date: '2026-11-28T11:08:00Z'
labels: []
milestone: m-4
dependencies: []
priority: high
ordinal: 191000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The live router has no 1M tier for the Flash Next model: its serving list is 256k-4, 256k, 256k-8 and 512k, and the engine capability ceiling is 524288, so a request asking for more than that is refused rather than served. The DeepSeek and GLM models on the same host already carry 1m tiers, so the shape exists. A candidate adds a fifth named tier, 1m at 1048576 tokens and one lane, with its own engine launch record, and raises that ceiling to match. The pinned image and pinned launcher already support the profile, and the config dry run loads. Applying it touches a live machine that carries every Pi session, so the apply waits on the owner's window. The candidate files live outside the repository on the router host.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A candidate host config declares a 1m tier at 1048576 tokens and one lane for the Flash Next model and raises the engine ceiling to match.
- [ ] #2 The pinned image profile preflight passes for 1m and reports the derived context and rotary settings.
- [ ] #3 The config dry run exits zero and lists the new tier among that model's serving tiers.
- [ ] #4 The tier's launch record is readable by the container user and carries the 1m context and rotary settings.
- [ ] #5 After apply, the admin status view shows the 1m tier for that model with no engine switched and no session dropped.
- [ ] #6 One request whose required context exceeds 524288 completes through the 1m tier.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Candidate built from the live 256k-4 launch record: the context value is replaced and the managed rotary triple for the profile is added to the record's three arg lists, the same rule that makes the existing 512k record larger than the 256k one. Profile contract for 1m is context 1048576 with YaRN rope-scale 4 over a 262144 trained window, single lane. The pinned image's own profile preflight was run for 1m (read-only container, no GPU, no port, no model mount, no backend started) and reports derived args exactly so with effort position end and preflight passed. The config dry run exits zero and lists 1m(1048576tok/1slot) for the model. Permission measurement: a launch record mode 600 owned by the config owner gives the container user Permission denied, and mode 644 inside the mode-700 config directory reads fine, so the file is left 644 unless the owner prefers a chown to the handler account. A loader check found nothing cross-validates the engine capability ceiling against a tier's context, and admission matches a request's required context against the serving ladder, so the ceiling raise is consistency with the other engines rather than the gate that makes the tier selectable. Memory: the 256k-4 tier runs with a 76 GiB resident budget and no container memory limit; the host has 121 GiB total and 50 available with that engine up, so one 1M lane should sit in a similar envelope but is unmeasured until first launch. Applying through the reload route rather than a restart is untested for a new tier plus a raised ceiling, and the deployed binary still predates the containment fix, so this waits for the same window as that deploy.
<!-- SECTION:NOTES:END -->
