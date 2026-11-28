# Wave 1 implementation evidence — 27 November 2026

The owner approved repository integration on 28 November. PR #92 landed the exact tested aggregate, including the original heads of #86, #88, #89, #90 and #91. Its [main integration](https://github.com/ArborealML/Inference-Router/commit/1bf85d3495ab2517a921d78013ffe69bbefb4325) has tree `e2bdb07dc486bf969e4ee02f7dbb7b185305c55a`. The checks below apply to source and controlled fixtures. Live deployment and provider qualification are separate.

## Published implementation PRs

GitHub Actions checked their synthetic merge refs; independent readback verifies that each tested merge tree equals its PR head tree. Every run passed Release build with zero warnings/errors, the full test suite with zero failures/skips, Linux x64 Native AOT/config smoke, strict Backlog checks and the release-gate harness. The original local tests below remain scoped historical evidence.

| Task | PR / exact published head | Hosted tests | Successful CI run |
| --- | --- | ---: | --- |
| 203 | [#88](https://github.com/ArborealML/Inference-Router/pull/88) · `cb595b2d96646fe038261ff57f6041e813aacd6f` | 2,160 | [Run 46112027567](https://github.com/ArborealML/Inference-Router/actions/runs/46112027567) |
| 187 | [#89](https://github.com/ArborealML/Inference-Router/pull/89) · `7e42e0fca22565f61294b874de85338daca69250` | 2,193 | [Run 46112187952](https://github.com/ArborealML/Inference-Router/actions/runs/46112187952) |
| 205 | [#91](https://github.com/ArborealML/Inference-Router/pull/91) · `658c213e09a9ce75fcec3b3b9eda05558c8fd026` | 2,166 | [Run 46112352768](https://github.com/ArborealML/Inference-Router/actions/runs/46112352768) |
| 202 | [#90](https://github.com/ArborealML/Inference-Router/pull/90) · `ac9d783b8fcc84c4fd95278953f2fceda0af932f` | 2,269 | [Run 46112270273](https://github.com/ArborealML/Inference-Router/actions/runs/46112270273) |
| 204 | [#92](https://github.com/ArborealML/Inference-Router/pull/92) · `1f2acc65304549c196204a6ac126303fa87e2cf0` | 2,341 | [Run 46112396399](https://github.com/ArborealML/Inference-Router/actions/runs/46112396399) |

[Machine-readable original → prepared → published mapping](wave1-publication-map.json) records the complete reproduced graph, original authors, exact trees and ordered parents. Connector-generated Git identities use the owner's explicitly authorized exception; source authorship remains GPT-6.2 Sol, with GPT-6.2 Astra coordination, Backlog and distinct independent review. This mapping records publication before integration.

## Original evidence and prepared source

The original source hashes below identify the commits actually tested and independently reviewed during implementation. Two unpublished fixture commit subjects used `test:` rather than the repository's permitted `fix:` prefix. Prepared history corrects those subjects, preserving every source tree, raw author line and parent relationship under the mapping. Reissued commits use the coordinator committer identity. No published history was rewritten. Original development history and the full mapping are retained in the delivery packet.

| Task | Original reviewed range | Equivalent prepared source range | Focused result |
| --- | --- | --- | --- |
| 203 | `5beaaa70fde5f8377a91e3c0a43c9c82cffc82f0..9f023f30e9b5660bafed64c8fccd1b9f3accf3a7` | `5beaaa70fde5f8377a91e3c0a43c9c82cffc82f0..68969fcaeba654ad09947a12cbe3a3ffa417516a` | 263 passed |
| 187 | `934e7907df6a513fa43d9a9825df1e7a279d4150..56b24d3cbb21e2554454cc8931f20d03dc175488` | Unchanged | 150 passed |
| 205 | `6b72a7529963675bae50a12a366690c07e8bd6a8..f0f110e2fe96674a10dd2e8270286751181f2193` | `466aca8960f90467c3321d1e2d39b9e79fa00606..5727c995cf972784d73da90343b29795cef81a4c` | 105 passed |
| 202 | `8a12ba8ec218971aa0f9e992335dc33b1dc19dc8..ee2fbc8ee2ff7b321a0d2a6d4733cb1f5aaea93f` | Unchanged | 92 passed |
| 204 | `e641bee13427cc2e59b595d7ce96e08ba529ed49..95e800923040584da4e7793747a807536693e221` | `6aa1e4b5843212d2fe755fd86d2e470e77ae6abb..3aa454a073c43f55d22ce0bd69b7002f16b6c879` | 166 passed |

All focused runs used Release builds, warning-as-error analyzers and controlled fixtures, with no failures or skips at the named final source. Source-equivalent prepared hashes are not represented as the original execution hashes. The final combined run below executes the prepared history itself.

| Prepared branch | Receipt head | Intended review dependency |
| --- | --- | --- |
| `ready/task203-independent-progress` | `a275186e8bf16373bb14794ff5553b5a984a60d9` | Planning main |
| `ready/task187-semantic-observer` | `1d2eebb1fa4c7b692e791392358327f071c76541` | Planning main |
| `ready/task205-inherited-priority` | `1e958089f8b63610c94808e0543a5912cfbe16f3` | TASK-203 prepared head |
| `ready/task202-admin-json` | `2bb32ef10f29fe026b062598a30582b7bb4f3a7d` | PR #86 plus planning main, reconciled base `2c54500d72d6d726a259dcb58a6472346b44254d` |
| `ready/task204-stateless-admission` | `89783fbaf83bb31de3580030917a8ccf67f779ff` | Combined prepared integration `de6b86d55f0459db4ee191e64dcebcffe34d3742` |

## Combined qualification

Prepared aggregate `89783fbaf83bb31de3580030917a8ccf67f779ff`, tree `e2bdb07dc486bf969e4ee02f7dbb7b185305c55a`, passed **680 tests, zero failures, zero skips**. The named union covers 32 affected fixture classes across scheduling, exact admission, lifecycle groups, reload, priorities, semantic observations, wire/admin behavior and actual CLI capture processes. This was an integration-focused run, not the full suite. The command/filter, TRX and logs are retained in the delivery packet. SDK: the repository-pinned .NET 11.0.100. Existing selector-differential opt-out matches CI; no warning, assertion, deadline or acceptance gate was weakened.

Linux x64 Native AOT publish passed at the same prepared aggregate with zero compiler or trim warnings, including expanded trim diagnostics. The resulting native binary passed the existing configuration-only smoke against samples/router.ci-smoke.toml without opening sockets. Binary size: 24,038,232 bytes; SHA-256: c42647c93b6a06a3d10ca424736638b1eeb6975df4c8dd17e9cbb65b30a32c8a. This is local Linux x64 evidence; the published equivalents subsequently passed hosted full-suite/AOT checks as recorded above. Other platform release qualification remains unperformed.

Independent source/integration review covered the shared ticket projection, terminal observations, queue order and retained PR #86 parser. The publication audit covered 43 mapped commits, including 27 reissued objects, and all seven prepared heads. Every tree and raw author line matched; only the two declared subjects changed. A later Backlog-only receipt does not imply a new runtime execution.

## Findings and dispositions

- TASK-203: corrected a post-probe race where an earlier alternative became eligible while a later request claimed their last shared thread slot. Release shares the router gate; revalidation remains scoped to competing routes. Deterministic regression and follow-up review passed.
- TASK-187: retained fully consumed structured error evidence when later same-event data is malformed or over-depth, while discarding partial positive evidence. Byte-preservation and error-precedence regressions passed follow-up review.
- TASK-205: the initial source passed 104 tests. Integration exposed a fixture that mistook a scheduling state change for a send and raced logical capacity cleanup. The test now waits for actual cleanup and asserts exact selection; a deterministic interleaving regression was added. Corrected source passed 105 tests and combined integration passed 608 before TASK-204.
- TASK-202: bounded source review found no blocking defect; existing target-reset error code, authorization and ETag behavior remain preserved.
- TASK-204: an initial 160/166 run exposed invalid identity mapping to HTTP 500. The header-validation catch now explicitly returns 400; all six failing assertions stayed unchanged. The corrected 166-test run and separate correction review passed.

## Integration order and remaining work

PRs #88 (TASK-203) and #89 (TASK-187) target main independently. PR #91 (TASK-205) stacks on #88. Preserve PR #86's exact head `a12012b176e84e49603c433a9f934206499b4bcb`; PR #90 (TASK-202) stacks on its reconciled base. PR #92 (TASK-204) is reviewed against the combined integration dependency so its own diff remains focused. The exact aggregate was subsequently retargeted to main. Its synthetic merge tree equaled the tested tree.

PR #83 remains separate at `6fbbd2e3abdac6f1ced4edb6a6d146fad604ea21`, with branch-only progress preserved. Its additive ticket-progress fields and SPEC need a later bounded reconciliation. PR #86's outstanding TASK-185 review/client work is not closed here.

TASK-203 remains partial for commercial backoff/account limits, TASK-206's injected preparation scope and full available-route diagnostics. TASK-187's commercial/Tune consumers remain downstream. TASK-170 waits for recoverable prior fitter source; reported historical checks are retained without inventing a new runner. TASK-206/195, policy, commercial integration, numeric context, clustering and Tune adoption remain future implementation. No installed-client, hardware, account or live-deployment qualification was performed.

Backlog CLI and the repository's external review/tmux tooling were unavailable. Controlled Markdown updates, the tracked strict board checker, native parallel subagents and a distinct reviewer were used. Groma maintenance was expressly waived. This session created no parallel issue tracker and made no live host, lifecycle, paid-provider, account or deployment changes.

## Historical implementation assignments and handoffs

The 28 November [main integration](https://github.com/ArborealML/Inference-Router/commit/1bf85d3495ab2517a921d78013ffe69bbefb4325) includes #86 and #88–92. PR #83 remains open for service lifecycle corrections.

The following 27 November publication snapshot was pinned to planning main `62f1c79`. The coordinator is the sole Backlog writer. At most one primary and one independent companion implementation run; preparation and independent review may proceed alongside them. Native subagent messages and isolated worktrees replace the unavailable local tmux service in this session. Its authorization covered publication.

| Workstream | Current assignment and next handoff |
| --- | --- |
| A — Scheduling/admission | TASK-203/205/204 published as PRs #88/#91/#92. Independent source review and exact-head hosted CI passed (2,160/2,166/2,341 tests respectively). Coordinator owns remaining acceptance. Own queue traversal/take/wake contract. |
| B — Physical authority | Prepare TASK-206 seam and TASK-195 identity from existing primitives; TASK-25 owns paired production witness with 118. No production composition claim yet. |
| C — Protocol observation | TASK-187/202 published as PRs #89/#90. Independent source review and exact-head hosted CI passed (2,193/2,269 tests respectively); PR #86 remains preserved. Own semantic observation contract consumed by D/E. |
| D — Commercial | Consume PR #89 observation and #88 traversal through explicit dependencies; extend one transport/reservation path. Fixtures only. |
| E — Tune | TASK-170 preserves existing owner and reported fitter evidence; waiting for recoverable worker handoff. No duplicate runner dispatched. |
| F — Policy/Pi | Prepare 207/208 external-authority fixtures; preserve existing priority parser work and numeric/legacy compatibility boundaries. |
| G — Integration/review | Reconciled 83/86 overlap and CI; completed bounded physical-contract inventory, A/C plan/source reviews, integration review and publication-history audit. Exact published-tree/parent/provenance audit and PR-base review completed; final Backlog review follows. |

TASK-203/187/205/202/204 plans received independent passes with bounded corrections before code. PR #83's source auto-merges at the pinned base but SPEC needs reconciliation; PR #86's source merge is clean at that base, and its outstanding TASK-185 review corrections remain open. Preserve both branches and their historical checks. The pinned .NET SDK is installed and focused Release builds/tests run locally. Backlog CLI remains unavailable; controlled Markdown updates use the strict board checker. All five published heads passed hosted build/full-test/Linux-AOT, board and release-harness checks. TASK-220 records the owner's explicit implementation-session connector exception with Sol/Astra provenance; publication may proceed without another permission request. These checks preceded integration.

The final prepared aggregate 89783fbaf83bb31de3580030917a8ccf67f779ff passed 680 affected Release tests across 32 named fixture classes, with no failures or skips. Local Linux x64 AOT passed without compiler/trim warnings; the native configuration-only smoke passed. Independent source/integration and publication-history reviews passed. Earlier integration 02087978 passed 608 tests after a deterministic priority-fixture cleanup/selection correction; runtime assertions and deadlines were preserved. The aggregate preserves PR #86 and leaves #83 separate. The exact source mappings and qualification evidence above distinguish historical reviewed hashes from equivalent prepared commits. No task is closed by fixture receipts; the published equivalents passed hosted CI; remaining operational qualification is tracked separately.
