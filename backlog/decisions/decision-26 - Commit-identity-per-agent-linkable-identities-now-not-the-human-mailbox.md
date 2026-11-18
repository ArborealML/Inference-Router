---
id: decision-26
title: >-
  Commit identity: per-agent linkable identities now, not the human mailbox
date: '2026-11-18T01:50:00Z'
status: accepted
---
## Context

Decision-24 settled the identity question as "leave the published history, keep using the repository's configured
identity, add agent identities later." The owner has revised that ruling: not later — now. The current arrangement
puts an agent's work under a human's no-reply mailbox, which makes an agent commit indistinguishable from the owner's
own, and that is the property the earlier incident made visible rather than caused.

## Decision

Commits carry **per-agent linkable identities**: each agent session commits as its own identity with a real, resolvable
address, configured once in the agent's tooling rather than per commit, and the working contract names which identity a
session must use. The published history is still not rewritten: the two agent-named and three role-named outliers stay
exactly where they are, below a recorded floor. The push-range identity check survives, reworded: every commit in a push
range must carry either the owner's identity or a sanctioned agent identity, and anything else fails the build.

## Consequences

Attribution becomes true without a human absorbing an agent's work: a bad commit is traceable to the session that made
it, and a good one is credited to the right actor. It costs setup — an identity per agent kind, its configuration in the
tooling that runs sessions, and a rule that no session may set an author ad hoc — and it means the guard can no longer
compare against one expected name; it compares against a set. Nothing about the release path changes: tags and releases
are cut by the same principal as before, and the tag ruleset is unaffected.
