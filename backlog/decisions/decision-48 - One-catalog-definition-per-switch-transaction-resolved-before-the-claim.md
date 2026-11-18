---
id: decision-48
title: One catalog definition per switch transaction, resolved before the claim
date: '2026-11-18T14:24:00Z'
status: accepted
---
## Context

The commit review of v0.12.42 removed the selector ask inside `RunSwitchAsync` as unreachable. It was
reachable: the check in front of the claim read the definition the runtime was constructed with, while the
switch re-read the config collection by name (`EngineRuntime.cs:685` at `0dafcd2`). A same-name definition
handed to the public constructor, or an entry swapped between the two reads, put a runnable selector in
front of the claim and a blank one inside the transaction — the caller was answered `launch_failed` and the
engine's last proof was thrown away, the exact cost the refusal exists to avoid.

## Decision

A switch transaction resolves its `EngineDefinition` **once**, in front of the claim, from the config
collection (falling back to the constructed definition), and passes that object down. Nothing on the path
re-reads `_config.Engines` by name afterwards. Re-asking the question later is not a fix: the later ask is
still reading the second source.

## Consequences

The refusal and the transaction read one definition object. `CanonicalModel` (`:1249`) is the other
`_config.Engines` reader and sits off this path. Unifying the definition was not enough by itself — the
model's entry lookup stayed split, which decision-49 closes — and the pin that holds this asserts both
divergence directions (TASK-94 #11).
