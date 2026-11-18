# Router delivery milestones

Start with a real pi turn on the deployed router. The next end-to-end proof is a native model swap on one host. Fleet routing adds a second host and a shared admission decision. The milestone numbers are board identifiers. Several tracks can advance at once.

| Outcome | What must work |
| --- | --- |
| [Pi on a running router](m-0%20-%20pi-on-a-running-router.md) | Select a verified model in pi, discover its effective context, and finish a streamed turn through the deployed router. Qualify the second model in the picker. |
| [Bounded queue admission](m-1%20-%20bounded-queue-admission.md) | Refuse impossible work promptly and give queued work bounded memory, time and refusal events. |
| [Completion stream integrity](m-2%20-%20completion-stream-integrity.md) | Preserve upstream frames across arbitrary splits. A stalled stream releases capacity and reports a bounded error. |
| [Reliable switch and shutdown state](m-3%20-%20reliable-switch-and-shutdown-state.md) | Superseded probes cannot authorize work. Shutdown settles waiters with one cause, including under deliberately reproduced races. |
| [Native model swap through pi-clm](m-4%20-%20native-model-swap-through-pi-clm.md) | Match the local selector configurations, drain the incumbent and prove exact target readiness. Observe the resident change between real pi turns on A and B. |
| [Measured model scheduling](m-5%20-%20measured-model-scheduling.md) | Measured costs change placement decisions. Serve the eight B requests, then A, while bounding wait under continuing arrivals. Measure lock changes after the pi swap proof. |
| [Pi ticket tracking and outage recovery](m-6%20-%20pi-ticket-tracking-and-outage-recovery.md) | Follow and cancel a queued ticket, consume a valid readiness reservation, and wait through a bounded outage in the local proxy. |
| [Configuration reload during inference](m-7%20-%20configuration-reload-during-inference.md) | Apply a validated configuration generation while active work keeps its claimed configuration. An invalid change leaves the current generation serving. |
| [Cluster routing and model placement](m-8%20-%20cluster-routing-and-model-placement.md) | Either ingress admits against one fleet queue. A managed peer activates the selected model and completes inference. The host can refuse unsafe placement. |
| [Verifiable public release](m-9%20-%20verifiable-public-release.md) | Verify the canary, then publish and identify the stable Linux and Windows binaries with downloadable evidence. |
| [Operator live view](m-10%20-%20operator-live-view.md) | Bootstrap a bounded status view and follow ticket progress with the replayable event feed. |
| [Inference API qualification](m-11%20-%20inference-api-qualification.md) | Qualify adapter choices against actual client behavior, including a real tool round before introducing a Responses surface. |
| [Repository and architecture checks](m-12%20-%20repository-and-architecture-checks.md) | Pin the lifecycle map, Git identities, board validation and coordinator handoffs with reproducible checks. |

## Dependencies that determine the order

- **The first native swap** needs coherent model and context evidence (76 → 68), provider primitives (26), host exclusivity (110), and exact-target verification (118) before the local configuration can qualify (111). Admission and drain (25, 64), cold activation (114), and client keepalives (108) also feed the real swap proof (112). Provider fakes and offline configuration work can proceed while the live test waits for an authorized host.
- **Batch scheduling** consumes bounded queue waits (51) and measured activation and shutdown times (71). The lock cleanup (113) starts after the pi swap (112), with the same workload available for comparison.
- **The cluster** needs separate transport and lifecycle authority (77), a host capable of refusing unsafe work (110), and fleet-wide admission, cost and fairness (78, 71, 72). One ingress has already reached a resident model on another host. The remaining proof requires both ingresses, shared admission and managed peer activation. Use protected ingress for that test. Production exposure also needs the security controls in SPEC §14.1.
- **Reload** can be designed alongside clustering. Its immutable configuration generations must agree with exact-target verification. A fixed configuration suffices for the initial native swap and two-host proof.
- **Release, tooling and UI** have independent deliverables. The retained canary provides evidence for the stable release gate. The release badge waits for the stable release. Operator views consume bounded, replayable observations.

The [SPEC](../../SPEC.md) describes the interfaces and acceptance bar. Its historical roadmap predates the native switching decision and the active fleet tasks. The milestone descriptions cite the relevant sections, and each task retains its own prerequisites and acceptance criteria.
