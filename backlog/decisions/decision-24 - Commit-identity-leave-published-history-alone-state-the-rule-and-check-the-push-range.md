---
id: decision-24
title: >-
  Commit identity: leave published history alone, state the rule, and check the
  push range
date: '2026-11-18T01:20:00Z'
status: accepted
---
## Context

Five commits on the main branch of a public repository carry an author name that is not the repository's
configured identity: two carry an agent-model name with an unresolvable address (so they render as unlinked
authors) and three carry a role name on the account's own no-reply mailbox. The cause is a per-commit override by
an earlier session, not the repository configuration, which is correct and was used by every other commit. Three
options were put to the owner: leave the published history and prevent recurrence, rewrite the two author entries
and force-push, or attribute agent work deliberately to a real linkable identity. Rewriting was mechanically easy
because the main branch is unprotected — the only ruleset guards release tags — which is precisely why the choice
was the owner's and not an agent's.

## Decision

The owner chose, verbatim: **"I do like A, then C later."** So: published history is left exactly as it is — no
rewrite, no force-push, and none ever without the owner's explicit word; the rule is stated in the working contract
instead, which is that commits carry the repository's configured git identity and no session sets a per-commit
author for itself; and a check over the range being pushed enforces it in CI, naming any commit whose author or
committer differs from the configured identity. Option C — deliberate, linkable per-agent identities of the
agit-style kind — is accepted as a later act, not part of this one, and nothing in the current work assumes it.

## Consequences

The five odd authors stay visible in a public history permanently, and that is the accepted cost of not rewriting:
a force-push to an unprotected main branch to fix cosmetics is the worse precedent. A new CI check has to be
written, and it must cover the *push range* rather than one commit, so a rewrite cannot smuggle an override past
it. The contract gains one sentence, and the later attribution work stays a board task rather than an assumption
in the release path. Nothing in the release gate depends on author identity: the tag and release runs work under
the default author, which is why leaving history alone costs no functionality.
