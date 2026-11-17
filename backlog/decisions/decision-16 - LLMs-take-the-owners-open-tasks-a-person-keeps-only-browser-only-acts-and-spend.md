---
id: decision-16
title: >-
  LLMs take the owner's open tasks; a person keeps only browser-only acts and
  spend
date: '2026-11-17T10:48:00Z'
status: accepted
---
## Context

Nine open tasks carried `@claire` as assignee, most of them `Blocked`, which on this board means "a person
has to move". Decision-14 put four kinds of act with a person: browser-only acts, publishing,
shared-infrastructure mutations, and wire changes or their refusal. The owner overruled that split on
2026-11-17: *"All of the tasks currently assigned to Claire can be taken by an LLM. Some should go to
chatswarm, you can take the rest."*

## Decision

Every open task that carried `@claire` is reassigned.

- **`@chatswarm`** (the external research and review service) takes the calls that need option analysis or
  a posture judgement: the live-view shape and credential posture, what a `/v1` caller sees when a stream
  stalls, whether to verify what a downloader receives. Its answer is **adopted** unless it contradicts SPEC
  or something this code proves; the board records the headline and what it verified locally, and never pastes
  reviewer prose into a tracked file.
- **`@qwen3.8-flash-next`** (the local coding session) takes the acts that need only credentials this machine
  already holds: `git tag`, `gh api`, and repository settings reachable over REST.
- **`@claire`** stays an assignee only where no agent can physically act — a settings form with no API (on this
  board: the social-preview image and the organisation avatar) — and for anything that spends money.
  Decision-10 already refused paid macOS runner minutes; the same rule covers signing keys and any runner
  spend beyond what is already allowed.

## Consequences

`Blocked` now means an agent is stuck, not that a person is the bottleneck, and an `@claire` task in that
column should be rare and unmistakable. Publishing and shared-infrastructure mutations stay **reportable**
rather than forbidden: the agent performs them when the task says so and names in its report and in SPEC what
it changed on a public repository or a live host, so the change is reversible by inspection. A wire change is
still a wire change — it lands as a SPEC revision with tests, whoever decided it.
