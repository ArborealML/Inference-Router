---
id: decision-10
title: 'No paid macOS runner minutes; release artifacts are Linux and Windows only'
date: '2026-11-17T06:05:00Z'
status: accepted
---
## Context

`release.yml` declares four build legs — `linux-x64`, `win-x64`, `osx-arm64` (macos-14) and `osx-x64`
(macos-13) — and each leg smoke-tests its own binary before a release is published. In every run so far
the Linux and Windows legs built and verified, while the two macOS jobs sat queued and were never picked
up: the account has no usable macOS minutes. So the public repository advertises macOS binaries it has
never produced, and the release gate ("publish needs four verified assets") can never be satisfied.
TASK-15 exists to resolve that; TASK-13's last open criterion is the same proof.

## Decision

Options considered:

1. **Buy macOS runner minutes** and run all four legs, accepting the per-minute cost and the risk that
   the older x64 macOS image is retired before the account uses it.
2. **Drop the macOS legs**: release **`linux-x64` and `win-x64` only**, and stop claiming four platforms
   in the workflow, the README and the artifact/checksum matrix.
3. **Publish the macOS artifacts unverified** with a documented warning — rejected outright: an
   unverified native-AOT binary on the exact platform it was never built on is the worst combination of
   promise and evidence.

**Chosen: 2**, on the owner's instruction — no paid minutes for this project. The expectation going in was that macOS was trivial, and about the port it is:
the code cross-compiles and the legs were already declared. What is not trivial is the **evidence**. A
published native binary has to be built and smoke-tested on its own operating system, which needs a paid
macOS runner, and the older x64 macOS image the second leg named is on a retirement path. Trivial port,
non-trivial proof — that gap is the entire reason for a two-platform published surface. macOS stays supported in
principle (the code builds for it locally, and `dotnet publish -r osx-arm64` remains documented as a
build-it-yourself path), but nothing is published, claimed or gated on it.

## Consequences

- `release.yml` loses two matrix legs, and the publish gate moves from "four verified assets" to two;
  the `SHA256SUMS` matrix and the README's platform table and download copy follow the same two names.
  This is code and documentation work, not a board edit.
- TASK-15 resolves through its "dropped" branch: the decision is taken, the remaining work is making the
  workflow and the README stop claiming osx artifacts.
- TASK-13's open criterion ("all four RID artifacts build, verify, only then publish") no longer matches
  the intended release surface and has to be reworded or retired — that task body is the code session's.
- TASK-12's removed four-RID criterion stays removed: the four-OS claim is gone, so its carrier TASK-15
  closes by dropping the legs rather than by running them.
- Cost of the choice, stated plainly: mac users build from source or wait. Nothing in the router is
  Linux/Windows-specific, so this is a publishing decision, not a port.
