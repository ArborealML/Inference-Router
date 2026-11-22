# Router delivery milestones

The first milestone requires a completed pi turn on the deployed router. An isolated pi session has already completed turns on two resident models. The native swap milestone requires observing a host change its resident model between turns. The cluster milestone adds shared admission and activation from either ingress.

## Delivery evidence as of November 22

**Pi on a running router is complete (6/6).** The deployed router served streamed Qwen and Swift turns in the same Pi session across an operator-managed selector change. Model discovery and the pinned client agreed on Qwen’s 256,000-token window. [PR #8](https://github.com/ArborealML/Inference-Router/pull/8) qualified the remaining admission check when a serving window shrinks during selection. [PR #33](https://github.com/ArborealML/Inference-Router/pull/33) completed CLI prompt parsing for both verbs, including stdin when only options are supplied.

**Local queue-driven switching passed live and shipped.** Qwen completed before the original queued DeepSeek request drove the resident change and streamed. Docker, the backend and native GPU-process observations confirmed residency. Named resource groups support N clients and N configured engines. Authenticated idle catalog reload also shipped. Exclusive control across independent processes on a shared host remains open. [Live evidence](../../docs/local-model-switching.md#verification) · [Merged implementation](https://github.com/ArborealML/Inference-Router/pull/75)

## Delivery path

Stream integrity, switch safety and bounded admission can advance in parallel. Each later milestone depends on the specific tasks listed below.

| Outcome | What must work |
| --- | --- |
| [Pi on a running router](m-0%20-%20pi-on-a-running-router.md) | Select a verified model in pi, discover its effective context, and finish a streamed turn through the deployed router. Qualify the second model in the picker. |
| [Completion stream integrity](m-1%20-%20completion-stream-integrity.md) | Preserve upstream frames across arbitrary splits. A stalled stream releases capacity and reports a bounded error. |
| [Reliable switch and shutdown state](m-2%20-%20reliable-switch-and-shutdown-state.md) | Superseded probes cannot authorize work. Shutdown settles waiters with one cause, including under deliberately reproduced races. |
| [Bounded queue admission](m-3%20-%20bounded-queue-admission.md) | Refuse impossible work promptly and give queued work bounded memory, time and refusal events. |
| [Native model swap through pi-clm](m-4%20-%20native-model-swap-through-pi-clm.md) | Match the local selector configurations, drain the incumbent and prove exact target readiness. Observe the resident change between real pi turns on A and B. |
| [Measured model scheduling](m-5%20-%20measured-model-scheduling.md) | Measured costs change placement decisions. Serve the eight B requests, then A, while bounding wait under continuing arrivals. Measure lock changes after the pi swap proof. |
| [Cluster routing and model placement](m-6%20-%20cluster-routing-and-model-placement.md) | Either ingress admits against one fleet queue. A managed peer activates the selected model and completes inference. The host can refuse unsafe placement. |

## Parallel work

| Outcome | What must work |
| --- | --- |
| [Pi ticket tracking and outage recovery](m-7%20-%20pi-ticket-tracking-and-outage-recovery.md) | Follow and cancel a queued ticket, consume a valid readiness reservation, and wait through a bounded outage in the local proxy. |
| [Configuration reload during inference](m-8%20-%20configuration-reload-during-inference.md) | Apply a validated configuration generation while active work keeps its claimed configuration. An invalid change leaves the current generation serving. |
| [Verifiable public release](m-9%20-%20verifiable-public-release.md) | Verify the canary, then publish and identify the stable Linux and Windows binaries with downloadable evidence. |
| [Repository and architecture checks](m-10%20-%20repository-and-architecture-checks.md) | Pin the lifecycle map, Git identities, board validation and coordinator handoffs with reproducible checks. |
| [Hardware-specific inference tuning](m-14%20-%20hardware-specific-inference-tuning.md) | Plan the per-engine, per-model sweep of context, stable concurrency and speed, with comparable hardware records across upgrades. |

## Follow-on work

| Outcome | What must work |
| --- | --- |
| [Operator live view](m-11%20-%20operator-live-view.md) | Bootstrap a bounded status view and follow ticket progress with the replayable event feed. |
| [Inference API qualification](m-12%20-%20inference-api-qualification.md) | Qualify adapter choices against actual client behavior, including a real tool round before introducing a Responses surface. |

## Single-host comparison and fixes

The [single-host robustness baseline](m-13%20-%20single-host-robustness-baseline.md) records the comparison with NVIDIA Personal AI Router and the readiness, transport and error-handling fixes that shipped. SPEC §14.1 tracks the remaining parity gaps. The earlier v0.9 cohort contained tasks 1 and 15. The v0.10 cohort contained tasks 10, 12 and 13. Their primary milestones now reflect what shipped, so each completed task contributes once.

Five archived records retain their outcome assignments for reference. The board excludes archived records from its completion counts.

## Dependencies that determine the order

- **The first native swap** needs coherent model and context evidence (76 → 68), provider primitives (26), host exclusivity (110), and exact-target verification (118) before the local configuration can qualify (111). Admission and drain (25, 64), cold activation (114), and client keepalives (108) also feed the real swap proof (112). Provider fakes and offline configuration work can proceed while the live test waits for an authorized host.
- **Batch scheduling** consumes bounded queue waits (51) and measured activation and shutdown times (71). The lock cleanup (113) starts after the pi swap (112), with the same workload available for comparison.
- **The cluster** needs separate transport and lifecycle authority (77), a host capable of refusing unsafe work (110), and fleet-wide admission, cost and fairness (78, 71, 72). One ingress has already reached a resident model on another host. The remaining proof requires both ingresses, shared admission and managed peer activation. Use protected ingress for that test. Production exposure also needs the security controls in SPEC §14.1.
- **Reload** can be designed alongside clustering. Its immutable configuration generations must agree with exact-target verification. A fixed configuration suffices for the initial native swap and two-host proof.
- **Release, tooling and UI** have independent deliverables. The retained canary provides evidence for the stable release gate. The release badge waits for the stable release. Operator views consume bounded, replayable observations.

The [SPEC](../../SPEC.md) describes the interfaces and acceptance bar. Its historical roadmap predates the native switching decision and the active fleet tasks. The milestone descriptions cite the relevant sections, and each task retains its own prerequisites and acceptance criteria.

## Reading older board snapshots

The board sorts milestones by numeric ID. Commit `54d329c` reordered the IDs and assigned historical work to its delivery outcome. For a snapshot before that commit, use the following crosswalk. The task IDs stayed stable.

| Earlier ID | Current ID | Outcome |
| --- | --- | --- |
| m-1 | m-3 | [Bounded queue admission](m-3%20-%20bounded-queue-admission.md) |
| m-2 | m-1 | [Completion stream integrity](m-1%20-%20completion-stream-integrity.md) |
| m-3 | m-2 | [Reliable switch and shutdown state](m-2%20-%20reliable-switch-and-shutdown-state.md) |
| m-6 | m-7 | [Pi ticket tracking and outage recovery](m-7%20-%20pi-ticket-tracking-and-outage-recovery.md) |
| m-7 | m-8 | [Configuration reload during inference](m-8%20-%20configuration-reload-during-inference.md) |
| m-8 | m-6 | [Cluster routing and model placement](m-6%20-%20cluster-routing-and-model-placement.md) |
| m-10 | m-11 | [Operator live view](m-11%20-%20operator-live-view.md) |
| m-11 | m-12 | [Inference API qualification](m-12%20-%20inference-api-qualification.md) |
| m-12 | m-10 | [Repository and architecture checks](m-10%20-%20repository-and-architecture-checks.md) |

The progress display counts tasks whose status is Done. A milestone reassignment also updates the task’s edit timestamp. For a completion date, use the commit that changed its status to Done.
