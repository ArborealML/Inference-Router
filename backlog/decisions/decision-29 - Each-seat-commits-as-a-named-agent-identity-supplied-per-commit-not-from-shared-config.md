---
id: decision-29
title: >-
  Each seat commits as a named agent identity, supplied per commit, not from
  shared config
date: '2026-11-18T03:34:00Z'
status: accepted
---
## Context

decision-26 ruled that commits carry per-agent identities rather than the human mailbox, and the owner
fixed the name form as "Arboreal Agent (model-id)". Asked for the last two facts — the address that
makes the identity resolvable, and whether a seat tag is added when two sessions run the same model —
the owner answered "I don't much care, so do your recommendation". Two facts about this repository make
the obvious implementation wrong. Both seats commit in **one shared worktree**, so a single
`git config` cannot carry two identities. And no bot account exists: any address invented here is
either a fabricated GitHub noreply address, which is false, or the human's, which is what decision-26
rejected.

## Decision

- **Names.** `Arboreal Agent (q38fn · board)` for the manager seat, `Arboreal Agent (q38fn · impl)` for
  the implementer seat. A new seat takes the same form with its own model id and seat tag.
- **Addresses.** A stable handle under a reserved, never-resolving domain: `agent-q38fn-board@arboreal.invalid`
  and `agent-q38fn-impl@arboreal.invalid`. The domain is RFC 2606 reserved, so it cannot deliver mail and
  cannot belong to anyone else. It is a handle, not a mailbox, and it does not pretend to be a GitHub
  noreply address.
- **How it is applied.** Per commit, with `git -c user.name=… -c user.email=…`, or the equivalent for the
  tooling that starts a session. Never `git config` in the shared worktree — that would give both seats
  the same identity — and never a bare model name in place of an identity.
- **The gap stays named.** GitHub linking an author (avatar, clickable profile) is not achieved by this
  and is an accepted gap until the owner mints a bot account and swaps the address; swapping is
  configuration, not a history rewrite.
- Published history keeps the identity it already carries (decision-24: no rewrite), and the push-range
  guard checks a range against the sanctioned **set**, not against one name.

## Consequences

A pushed commit says which seat, on which model, made it, without borrowing a person's mailbox or
inventing a fake one. Adding a seat is one entry in the sanctioned set. The cost is honest and small:
author lines will not resolve to a GitHub profile until a bot account exists, and that act is the
owner's whenever they want it.

One breach is on the record: a board commit carried the machine identity because the commit command omitted these flags. It is pushed, so correcting it is the owner's call (decision-24) and is tracked as its own row. Until that answer lands, a mismatched identity in pushed history is a known gap rather than a mystery, and the seat's guard is mechanical: the flags go in the command, not in shared config.
