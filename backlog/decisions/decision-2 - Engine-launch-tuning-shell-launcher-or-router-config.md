---
id: decision-2
title: 'Engine launch tuning: shell launcher or router config'
date: '2026-11-16T20:40:00Z'
status: proposed
---
## Context

Container launch flags today live in shell launchers: context sizing, token-total scaling, GPU
reservations, memory headroom, and feature toggles. Those values encode measured tuning from real
hardware. The selector only validates and calls the launcher; it does not know what the flags mean,
which is why proving readiness afterwards has to re-check the engine instead of trusting the launch.

If the router is to preflight a switch and prove readiness before traffic, it needs to understand the
parameters that determine the serving shape: context window, slot count, modalities, and experimental
feature toggles.

## Decision

Options:

1. **Keep the launchers.** The native provider validates, stops the previous backend, and calls the
   existing launcher for the start step only.
2. **Move launch parameters into router config.** The provider builds the container itself from a
   declarative launch spec, and the same spec feeds preflight and readiness proof.
3. **Hybrid.** Config owns everything readiness must prove (context, slots, modalities, feature
   toggles); provider code or a launcher handles hardware-specific tuning the config cannot express.

Recommended: 3, with the boundary drawn at what readiness proof can verify. Anything the router cannot
verify stays outside the router, and the existing exec path remains an escape hatch for whole backends
that are not modelled yet.

## Consequences

- Option 1 is the smallest change and keeps tuning where operators already edit it, but the router can
  never preflight a launch it does not understand and its readiness checks stay post-hoc.
- Option 2 gives one source of truth and real preflight, but the config becomes the risky artifact:
  a wrong value can stop a working backend before the mistake is discovered, and tuning history has
  to be re-derived and re-measured.
- Option 3 needs the boundary documented in the spec, or it decays into both models disagreeing.
- In every option, ownership labels and the stop-others policy stay in the router: that is the safety
  rule, and it cannot live in a script the router cannot see.

