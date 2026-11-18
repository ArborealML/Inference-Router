---
id: decision-38
title: >-
  An external setting is verified by the bytes it serves, not by a host name or
  a form control
date: '2026-11-18T05:50:00Z'
status: accepted
---
## Context

Three owner answers of `card set` were read as failures. The check written into TASK-29 tested the **host** of
the repository page's `og:image` — `opengraph.githubassets.com` would mean our upload had taken,
`repository-images.githubusercontent.com` would mean GitHub's generated card — and its steps told the owner to
press Save in a pane that has no Save control. Re-measured 2026-11-18T05:50Z: the page serves a
`repository-images.githubusercontent.com/1460450305/…` address, and that address fetches as 38,150 bytes,
1280x640, sha256 `cee3237b7f98…98f09` — byte-identical to `docs/images/air-social-1280x640.png` as committed
at b214ce6. GitHub applies a social-preview upload the moment the file is accepted and serves a custom image
from the same host as its generated one. The card has been live since the first upload; three correct acts
were reported as failures by a proxy.

## Decision

An external, browser-only setting is verified by what a fetch from outside the form returns — exact bytes, byte
size, sha256, or an exact value — not by a host name inside a URL, a thumbnail rendered in a settings pane, or
the presence of a control the product does not have. An owner-facing task that asks for such an act carries the
artefact, its measurements, and the single command that fetches what the public surface serves and hashes it.

## Consequences

Readbacks become commands this seat can run, so a mismatch says the act is open rather than the owner answered
wrong. A Done that rests on a proxy gets re-run as a measurement before it is trusted: TASK-46 AC #5 was
reworded on this finding at 03:23Z and TASK-29's steps are corrected here. Checks become slower to write and
harder to argue with.
