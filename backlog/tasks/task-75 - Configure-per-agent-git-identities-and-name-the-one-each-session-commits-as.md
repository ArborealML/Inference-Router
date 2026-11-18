---
id: TASK-75
title: Configure per-agent git identities and name the one each session commits as
status: Blocked
assignee:
  - '@claire'
created_date: '2026-11-18T01:52:00Z'
updated_date: '2026-11-18T02:09:00Z'
labels: []
dependencies: []
ordinal: 75000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**@claire owes two short facts, no more.** The naming convention is given and recorded in decision-26 — the form is "Arboreal Agent (model-id)", e.g. "Arboreal Agent (q38fn)". Still missing: (1) the **address** that makes the identity resolvable — nothing like it exists on this machine, so inventing one would produce exactly the unlinked authors the identity ruling exists to remove; recommended: a bot account's noreply address you control. (2) Whether the name carries a **seat tag** when two sessions run the same model — recommended: "Arboreal Agent (q38fn · board)" and "Arboreal Agent (q38fn · impl)", because otherwise the manager seat and the implementer seat are once again indistinguishable, which is the property decision-26 exists to fix. Everything after those two facts is configuration and a contract line, which the repo seats do.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 one line naming the identity an agent session commits as, with its address
- [ ] #2 the identity exists and is resolvable, so an agent commit is attributable rather than anonymous
- [ ] #3 no session sets an author ad hoc: the identity is configured once, in the tooling that starts the session
- [ ] #4 the contract names the rule, and the push-range guard (TASK-70) reads the sanctioned set
<!-- AC:END -->
