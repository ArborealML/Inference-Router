---
id: TASK-8
title: 'External library review: AOT-safe Docker access from .NET'
status: Done
assignee:
  - '@chatswarm'
created_date: '2026-11-16T20:46:00Z'
updated_date: '2026-11-18T19:29:00Z'
labels:
  - external
milestone: m-4
dependencies: []
ordinal: 8000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
An external deep-review pass has been dispatched to settle which way the router should talk to Docker: the long-standing community client, the actively maintained fork used by cloud tooling, or hand-rolled Engine HTTP over the unix socket with source-generated JSON. The crux is whether each survives native AOT without reflection, plus unix-socket support, exec and events coverage, error fidelity between 'absent' and 'unreachable', and Engine API version pinning.

**Result (external review; the full digest stays outside the repository)**
Verdict: **do not use a .NET Docker client library under Native AOT.** Talk to the Docker Engine
HTTP API directly over the socket: `SocketsHttpHandler.ConnectCallback` +
`UnixDomainSocketEndPoint` (npipe on Windows via `System.IO.Pipes`), `HttpClient` based at
`http://docker/`, System.Text.Json **source-generated** contexts for inspect/create/exec/event DTOs,
API version pinned (`/v1.44`) with `/_ping` + `/version` negotiation and a min/max daemon range.

Interface shape: extract `IEngineLifecycle` from the switch path and inject a `DockerLifecycle`
over a fakeable, cancellable `IContainerRuntime` (`Inspect`, `List`, `InspectImage`, `Create`,
`Start`, `Stop(grace)`, `Kill`, `Remove`, `Exec → {stdout,stderr,exit}`, `Logs`, `Events`).
Error fidelity is the crux: **404 from inspect means absent and nothing else**; socket/transport
failures mean *daemon unavailable*, never absent; 304 is idempotent; 409 is a conflict to surface.
Exec output needs the 8-byte multiplexed frame header parsed and exit code 0 required. No pulls:
verify a pinned image (Id, RepoDigests, platform, layers) and refuse to run anything unpinned or
unlabelled. Podman speaks Docker v1.40 semantics; containerd-native is a separate gRPC adapter.
Docker group membership is root-equivalent — relevant to the authorization decision.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 A recommendation with package names, versions and an AOT verdict
- [x] #2 The chosen seam is faked in tests without a live daemon
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Proof: this was a request to the external research and review service, and its answer is mirrored in the repository rather than in a transcript - the disposition row in [SPEC 15 at line 1351](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md#L1351) (AOT-safe Docker access from .NET 11), which is what the code and SPEC follow. Where the answer was not adopted, the reason is in the same row.
<!-- SECTION:NOTES:END -->
