---
id: decision-21
title: >-
  A public version number is the owner's go-ahead; a test tag inside an
  agent-run protocol is an agent act
date: '2026-11-17T14:26:00Z'
status: accepted
---
## Context

decision-14 keeps "publishing — cutting a tag, making a release public" with a person. decision-16 hands
`git tag` and REST-reachable repository settings to the agent, and says a person keeps only no-API acts and
money. Both were being applied at once, which is how one question ended up parked three ways: TASK-32 assigned
to an agent while described as a decision, TASK-49 asking the owner about a ruleset POST, and TASK-42 asking
permission for a tag push.

## Decision

**Split by what reaches the outside world, not by which command runs.** A **release version number** becoming
public — the first tag, `v0.1.0`, making a draft release visible — needs the owner's go-ahead, recorded as a
one-line answer on an owner task; the agent performs the push and reports it. A **test tag inside an agent-run
protocol** (`v0.0.0-canary`, a rehearsal tag in a disposable repository) is an agent act: no version number
people depend on is published, and the act is reportable rather than permitted. Repository settings reachable
over REST — rulesets, immutable releases — stay agent work under decision-16.

## Consequences

TASK-49 asks one owner question (a second public repository) plus the go-ahead framing for the first release
tag on TASK-32. TASK-42's canary needs no permission: its body states the recommendation and the irreversible
consequence. TASK-38's ruleset scope is decided by the agent, with the reasoning written where the act is
recorded.
