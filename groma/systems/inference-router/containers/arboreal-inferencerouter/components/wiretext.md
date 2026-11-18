---
type: C4 Component
title: WireText
status: stable
groma:
  id: wiretext
  parent: arboreal-inferencerouter
  code:
    - scanner: csharp
      file: src/Arboreal.InferenceRouter/WireText.cs
description: Holds the budgets that bound what the router says, and the closed set of refusal categories a caller may be told.
---

Two rules that every reporting surface share, kept in one place so they cannot drift apart. The first is length: how much of one identifier — a model id, an engine name, a profile name — a caller-facing sentence or an event payload may carry, cut head-first with a marker that states how many characters were dropped, so a reader can tell a clipped value from a complete one. The second is vocabulary: the named, finite set of switch-failure categories a caller is allowed to hear, with the parameters that made the attempt fail left on the event feed for an operator instead of travelling in the caller's error body. The event ring caps entries, not characters, and every value these paths interpolate was chosen upstream — an engine's model answer, a provider's own words, the model id in the request body — so the ceiling is the router's and is applied to the pieces before a sentence is built rather than to the finished sentence. Both are constants and tokens on purpose: that is what makes 'bounded' and 'one of a named finite set' the kind of claim a test can walk.
