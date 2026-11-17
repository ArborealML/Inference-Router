---
id: decision-14
title: >-
  Four kinds of act stay with a person; every other Decision task carries an
  agent assignee
date: '2026-11-17T07:55:00Z'
status: accepted
---
## Context

The owner ruled that the queue of tasks assigned to them is workable by agents — "all of the tasks currently
assigned to Claire can be taken by an LLM, some should go to the external service, you can take the rest" — while
the coding session argued, from the working contract, that a task titled `Decision:` whose body walks a person
through a choice belongs to a person, because a question inside another task's body is not a blocker anyone can
act on. Both are right about different halves, and the board was silently absorbing the difference: `Decision:`
tasks sat assigned to agents with human-facing steps, and agent-owned tasks sat in Blocked with no human move
defined.

## Decision

**An agent owns the work; a person owns four kinds of act.** A task stays assigned to `@claire` only when the next
move is one of:

1. **Something only an authenticated browser session can do** — a GitHub settings upload, any web-form change with
   no API and no committed file behind it.
2. **Publishing** — cutting a tag, making a release public, or any act that puts a version number on the outside
   world.
3. **A mutation of shared infrastructure** — starting or stopping inference backends, driving a live daemon,
   installing a service or creating an OS account on the host.
4. **A wire-contract change, or the deliberate refusal of one** — anything that changes what `/v1/*` or `/admin/*`
   sends on the wire, including declining an addition the design recommended.

Everything else — including a task whose *title* begins `Decision:` — is assigned to an agent, and the agent
produces the analysis and a one-line answer rather than parking the question on a person. The external research
and review service (`@chatswarm`) owns only work whose deliverable is a **recommendation**; when its answer needs
an approval, the board files a separate owner task carrying the recommendation, its cost, and the one-line answer
format. A task assigned to `@chatswarm` without a dispatched request is a defect.

## Consequences

- The board distinguishes *design in flight* (Waiting, assigned to `@chatswarm`, body names the dispatch and the
  wake condition) from *ratification pending* (Blocked, assigned to `@claire`, body names the recommendation and
  the answer line). A single task rarely does both; when it does, it gets retitled from `Design:` to `Decision:`
  at the moment the design lands.
- A `Decision:` title is no longer evidence about the assignee, so the mechanical board check cannot infer one
  from the other. A `Decision:` task assigned to an agent is legitimate when the answer is derivable from facts —
  which is how the Docker-route question was closed without asking anyone.
- Deferring work is itself a disposition and gets recorded as one, with the trigger that reopens it; "we did not
  get to it" is not a disposition.
- Cost of the rule: an owner's in-tray is six one-line answers rather than zero. The benefit is that each of them
  is genuinely theirs, and the rest of the board moves without waiting.
