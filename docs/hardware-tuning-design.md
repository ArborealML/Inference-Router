# Hardware-specific inference tuning

The proposed `air tune run` would measure context capacity and concurrency on one operator-prepared engine and produce a hardware-specific record and advisory configuration. The run command, collector integration and workloads below describe a design. The example results are illustrative. AIR provides offline commands to plan a short grid from a saved config and compare two saved short-grid summaries on supported Linux x64 systems. [Manifest contract](../SPEC.md#112-offline-tuning-workload-manifests) · [Comparison format](../SPEC.md#111-offline-tuning-comparison)

## Choose an engine and profile

```text
air tune run --config tune.toml --engine ENGINE --model MODEL --profile TIER --max-concurrency 4 --host-record host.json --out run.json
air tune compare --baseline ram64-summary.json --candidate ram128-summary.json
```

Each run uses one explicit profile of one configured engine and model. To sweep a ladder, the authorized operator prepares an isolated instance for each supported tier and saves a separate run. The proposed command talks directly to that configured engine so router queuing, routing to another engine, and router admission caps are outside the measurement. Lifecycle, deployment approval, and any subsequent TOML changes stay with the operator. The comparison command reads saved records offline.

Today `air config` validates the engine catalog and prints declared profiles. `air chat` and `air complete` send single requests through the router. `air status` shows reported serving capacity and the CLI's `switch` sends an engine and model. The server can take an explicit profile for a switch, but its acknowledgment precedes completion of readiness checks. The proposed tuning record adds request-level timing and host evidence. [CLI dispatch and config readback](../src/Arboreal.InferenceRouter.Cli/Program.cs#L27-L120) · [Chat client](../src/Arboreal.InferenceRouter.Cli/Program.cs#L136-L242) · [Switch client](../src/Arboreal.InferenceRouter.Cli/Program.cs#L371-L398) · [Server switch handling](../src/Arboreal.InferenceRouter.Server/ServerApp.cs#L571-L621)

Resolve the engine-scoped model alias and exact tier from the parsed catalog. Read `max_context`, `max_slots` and the configured probe paths. For native Docker, the composed create projection supplies launch intent, including effective command, environment, bindings and resource requests. Treat the image lookup, local image ID, named repository digest and platform as separate facts. Compare the intent with the actual resident launch before measuring. An unloaded profile, unsupported launcher input, or mismatched resident yields an explicit refusal. In particular, a request to measure `96k` cannot borrow a resident `32k` launch. The current HY4 selector accepts `32k`; wider launch support and hardware qualification have their own requirements. [Config loader](../src/Arboreal.InferenceRouter/Config/RouterConfigLoader.cs#L190-L245) · [Exact target resolution](../src/Arboreal.InferenceRouter/Config/DockerTargetPlans.cs#L33-L44) · [Docker create projection](../src/Arboreal.InferenceRouter/Engines/Lifecycle/DockerLifecycleProvider.cs#L472-L537) · [HY4 setup](ai-integration.md#hy4-flash-on-the-shared-maya-listener)

The shipped HY4 setup uses 96k as nominal design sizing. The literal 65,536 rendered input plus 32,768 output split cannot fit at the pinned Maya window of 98,304 tokens because Maya reserves eight tokens. [Usable context envelope](#qualify-the-normal-use-context-budget)

## Generate a workload manifest offline

```text
air tune plan [--config FILE] --engine ENGINE --model MODEL --seed HEX --max-concurrency 4 [--profile TIER]
```

On supported Linux x64 systems, the offline planner reads the bounded saved
configuration and emits a versioned manifest for the short grid. With an explicit
canonical tier it emits one tier. With omission it lists the declared serving
ladder in ascending context and ordinal-name order, up to 16 tiers. The configured
default is a label. The seed is exactly 64 lowercase hexadecimal digits. The
configuration supplies nominal windows and configured slots. No runtime starts and
the command reads no credentials or contacts an endpoint.
[Manifest contract and file admission](../SPEC.md#112-offline-tuning-workload-manifests)

The manifest and the hash-pinned [generator definition](../src/Arboreal.InferenceRouter.Cli/AirTuneTextGenerator.v1.txt)
reproduce candidate user-text bytes from a seed and ordered coordinates. For
one coordinate the candidate can contain zero or more complete numbered
records and a byte prefix of the next record. The warmup uses its own derived
seed and is unmeasured. The actual warmup prompt, like each measured prompt,
must be counted and fitted. The one-record sample exercises generator bytes.
A future executor freezes the selected record count and tail, renders with the
verified template, and records exact corpus and request-body hashes. The
manifest contains neither fitted inputs nor those measured fingerprints.

## Verify the resident and isolate each wave

A future run requires explicit operator authorization for the isolated endpoint and a trusted, read-only collector. The host record supplies expected identity and collector trust settings. At preflight and before and after each wave, the CLI sends a fresh run or wave nonce and requires a current authenticated collector reply within five seconds. The collector binds the listener to the actual server and native child process, process or instance start identity, and launch generation. It attests the running binary and build, model weights and quantization, tokenizer and template, effective settings and overrides, effective memory, swap and CPU limits, ordered GPU assignments, and VRAM. Effective limits include constraints inherited from the parent scope. The design requires this evidence from the running instance, with coverage through wave settlement.

Read the configured model roster and serving gauges at the pinned origin. Require a single canonical loaded row, the exact observed target window and positive reported slots. The CLI uses the configured upstream credential only at that origin and disables redirects. An unknown, stale, mismatched or drifting identity or capacity makes the run inconclusive. A run without valid preflight issues zero workload requests. [Probe paths and model binding](../src/Arboreal.InferenceRouter/Engines/EngineRuntime.cs#L672-L802) · [Serving and admission contract](../SPEC.md#76-serving-profiles-and-context-window-escalation)

The first proposed adapter requires an enforced exclusive admission scope for this run and a witnessed idle baseline before preflight and each wave. Only that run's client may submit generation in the scope. Release exactly the requested number of owned streams together and witness that many service-admitted streams overlap. For the Maya adapter, sample processing slots at 2 Hz until the overlap is witnessed, within the request budget. The slot entries are anonymous and the in-flight counter includes queued work. Exclusivity and the idle baseline make service-admitted overlap attributable to the owned wave. Record the queue separately. Simultaneous native or GPU execution requires different evidence. A shared-engine adapter would require request-correlated admitted overlap before it could qualify a level. [Maya slots](https://github.com/mw00/project-maya/blob/05e0e2da90cacf174d791b0192f3488a3c61100b/serve/server.py#L6332-L6345) · [Maya queue accounting](https://github.com/mw00/project-maya/blob/05e0e2da90cacf174d791b0192f3488a3c61100b/serve/server.py#L3675-L3679)

On failure, stop issuing requests and settle the client's owned exchanges. Reserve 30 seconds within each overall budget for abort and drain. Require a fresh witness of idle, terminal owned work and an empty queue under the same scope. If that evidence is missing, end the sweep and record the untouched levels or long cell as untested. Process and service operations remain with the operator.

## Run the near-window concurrency grid

The proposed `near-window-text-v1` workload targets 512 output tokens and a rendered prompt within **[W − 671, W − 640]** for observed window **W**. The rendered count includes chat-template and special tokens. At nominal 96k, the top of the band is 97,664 prompt tokens. Use a fixed-seed text generator that asks the model to copy numbered ASCII records in order. Retain a resolvable immutable reference and digest for the versioned generator source or artifact. Its canonical inputs use a 64-character lowercase hexadecimal seed followed by tier, concurrency, wave, worker and line in fixed order, with decimal indices, unambiguous ASCII separators and LF line endings. The generator defines the exact record, nonce and warmup bytes. Retain the canonical inputs, the ordered corpus digest and each final request-body digest, including warmup.

Place each request-unique nonce at the earliest render-safe text position, before the fixed instruction and records, while preserving template framing. Verify actual native reuse at or below 64 tokens. The nonce reduces avoidable shared prefix and supplies no reuse measurement. Freeze greedy sampling and effective thinking settings. Leave tools, attachments, session history and resume out of the workload. Counts and timing characterize rendered prompt capacity and sustained decode under this workload. Copy fidelity, useful-context recall and output correctness are outside that evidence.

1. Limit concurrency to the minimum of the operator's bound from 1 through 8, the configured tier's slots, and the verified reported slots. Preparing a launch with higher slot settings is a separate operator action.
2. Use one unmeasured, uniquely seeded warmup at concurrency 1. Test consecutive integer levels from 1 through the cap. Run three measured waves at each level with a common start barrier and drain before the next wave.
3. Budget 600 seconds per request, 60 seconds of output idle after the first token, and 90 minutes per tier, including prompt preparation and drain. The largest grid starts at most 109 generation requests including warmup. Different approved time budgets produce a distinct comparison input.
4. A level passes when all three waves complete the exact workload within budget, witness owned active overlap, and preserve identity and capacity. Require complete terminal and native evidence and no errors, retries, crash, OOM, restart or unsettled work. Keep failed or partial results and stop starting new waves at the first failed or inconclusive level. Record skipped levels as untested with their cause.
5. Report the highest demonstrated stable concurrency. A pass at 2 and a failure at 3 leaves 4 untested. A pass through a cap of 4 reports “4 demonstrated, search capped at 4”.

## Qualify the normal-use context budget

A normal-use recommendation requires a separate opt-in `normal-use-long-decode-v1` cell. The proposed `--normal-use-concurrency N` selects the intended operating concurrency after it passes the short grid on the same verified launch. Use the same deterministic workload with **65,536 fully rendered prompt tokens** and **32,768 output tokens** per stream. Run one wave of at most eight streams with a three-hour deadline per request, a 120-second output-idle limit after the first token and a three-hour-ten-minute cell budget including preparation and drain. At 5 tokens/s, output alone takes about 109 minutes. Retain the long-cell samples separately from the short-grid peak.

Record nominal context, runtime reserve, usable total and output room at the exact rendered input. Pinned Maya 1.1.68 reserves eight tokens before applying reject or clamp logic. An oversized request can be shortened under `fit_max_tokens`. The long cell refuses it before generation. [Maya reserve](https://github.com/mw00/project-maya/blob/05e0e2da90cacf174d791b0192f3488a3c61100b/serve/server.py#L183) · [Context reject and clamp](https://github.com/mw00/project-maya/blob/05e0e2da90cacf174d791b0192f3488a3c61100b/serve/server.py#L3859-L3873)

| Envelope | Nominal context | Usable total | Output room after 65,536 rendered input |
| --- | ---: | ---: | ---: |
| Nominal Maya 96k | 98,304 | 98,296 | 32,760 |
| Minimum for the full split | 98,312 | 98,304 | 32,768 |

The full split is a workload qualification target. A shorter output, reduced prompt or missing long cell cannot qualify it. The 512-output grid remains independently useful at a nominal 96k.

## Check tokens, completion and native attempts

For the first Maya adapter, preflight the exact workload configuration with continuation-producing fair slicing and guard paths disabled. Verify the effective fair-slice interval is zero, the loop guard is off and the selected thinking settings cannot trigger a continuation. Maya can merge multiple native segments into one client request's timing record. If preflight cannot verify the required settings, report `unsupported`. If a run cannot distinguish a single native attempt from legitimate segments, mark the level inconclusive and keep it out of stability results. [Fair slicing, guards and segment accounting](https://github.com/mw00/project-maya/blob/05e0e2da90cacf174d791b0192f3488a3c61100b/serve/server.py#L4283-L4451)

Send the same effective messages, options, template, framing, thinking and sampling to the count path and the generation path. On Maya use `/api/tokens` with the same render-affecting inputs as `/v1/chat/completions`. Verify that generation-only shared overrides agree with the counted inputs. Unresolved overrides leave the workload unqualified. Record each accepted rendered count and request-body digest. [Maya count and rendering](https://github.com/mw00/project-maya/blob/05e0e2da90cacf174d791b0192f3488a3c61100b/serve/server.py#L3883-L3942) · [Generation settings](https://github.com/mw00/project-maya/blob/05e0e2da90cacf174d791b0192f3488a3c61100b/serve/server.py#L6786-L6809)

For the short-grid executor, freeze the count-request serialization and all
render-affecting fields before counting. The 8,388,608-byte count-input budget
is cumulative per prompt. Charge exact UTF-8 count-request body bytes before
each submission. With 24 calls the per-submission ceiling is 349,525 bytes.
Verify that the entire next record bounds the serialized size of every tail.
Form the initial inclusive prefix range from zero to the largest N whose N+1
full-record body fits that per-submission ceiling and the raw 8,388,608-byte
candidate bound. Without that sizing proof, end preflight. If even prefix one
cannot fit, record `invalid_workload` with no generation for that prompt.

Bisect that range with floor midpoints, at most 16 prefix counts. Retain the
highest counted value at or below the upper endpoint H, preferring the longest
candidate on ties. Counts below H search longer and counts above H search
shorter. For a retained count below H, bisect the full remaining next-record
tail range with up to eight counts. An already counted next full record needs
no repeat call. Even when nonmonotone counts make the search inconclusive, an
accepted body always has a measured in-band count. On call or byte
exhaustion, use a previously accepted in-band candidate only while its evidence
and the tier time outside the drain reserve still hold. Otherwise the prompt is
`invalid_workload` and sends no generation. A failed count consumes one call
and its charged bytes and ends the fit with the applicable native or transport
disposition. It supplies no search direction or retry. A tier deadline bars new
generation and leaves untouched levels untested.

A qualified warmup must finish with 512 output tokens, reason `length`, terminal
usage and one clean `[DONE]`, alongside matching counts and native attempt and
reuse evidence. Under the same otherwise qualifying evidence, 511 tokens with
reason `stop` is early termination. Drain that attempt and issue zero measured
wave requests. A warmup that meets all gates starts the first measured wave.

For each owned request require:

- The counted rendered prompt, native prompt count and terminal `usage.prompt_tokens` agree. Verify `timings.prompt_n + timings.cache_n` against that total as a consistency check. Maya derives those timing fields, so the sum alone is insufficient evidence. Require an independently present request-correlated native reuse count from 0 through 64 and reconcile it with the reported cache fields. Maya's JSON can show zero even when the native count was missing. HY4 measures native prefix reuse, which can exceed the workload ceiling. [Native parser](https://github.com/mw00/project-maya/blob/05e0e2da90cacf174d791b0192f3488a3c61100b/serve/server.py#L1290-L1297) · [Timing calculation](https://github.com/mw00/project-maya/blob/05e0e2da90cacf174d791b0192f3488a3c61100b/serve/server.py#L4973-L4987) · [HY4 prefix reuse](https://github.com/mw00/project-maya/blob/05e0e2da90cacf174d791b0192f3488a3c61100b/src/hy4/hy4_main.cpp#L641-L667) · [Native DONE producer](https://github.com/mw00/project-maya/blob/05e0e2da90cacf174d791b0192f3488a3c61100b/src/hy4/hy4_main.cpp#L855-L860)
- One correlated terminal usage record for the canonical model with exactly 512 or 32,768 completion tokens as selected, finish reason `length`, one `[DONE]`, and no in-band error. Early EOS, reduced output or missing termination fails the workload. The workload does not rely on `ignore_eos`, which the pinned Maya server gates on native logit-bias support. [Output cap and EOS support](https://github.com/mw00/project-maya/blob/05e0e2da90cacf174d791b0192f3488a3c61100b/serve/server.py#L2666-L2682) · [Terminal usage](https://github.com/mw00/project-maya/blob/05e0e2da90cacf174d791b0192f3488a3c61100b/serve/server.py#L5196-L5205) · [SSE end and error frames](https://github.com/mw00/project-maya/blob/05e0e2da90cacf174d791b0192f3488a3c61100b/serve/server.py#L6828-L6845)
- Complete native attempt and error evidence tied to the owned request and process generation from send through terminal result. Require one successful native attempt and zero retries or errors. Maya can retry a pre-output nonfinite error and subsequently return clean SSE. A witnessed retry fails the level. Missing owned evidence leaves it inconclusive even when the stream completes. An adapter requires instrumentation capable of observing that scope. [Native retry path](https://github.com/mw00/project-maya/blob/05e0e2da90cacf174d791b0192f3488a3c61100b/serve/server.py#L4619-L4640)

## Read the performance evidence

| Metric | Definition |
| --- | --- |
| Peak aggregate output throughput | Sum successful terminal output tokens across each passing wave and divide by wall time from barrier release to last terminal frame. This includes prefill, queue and transport. Retain all three rates per passing short-grid level. Report the highest median and its concurrency, with the lower concurrency winning a tie. |
| Single-stream decode | Keep the three passing concurrency-1 samples and their median. Use the native decode rate with its interval and counter provenance. Maya's `predicted_per_second` uses the engine-generated count, which can differ from `predicted_n` or served output usage. |
| Prompt processing | Divide uncached prompt tokens by engine prompt seconds, retaining reuse, raw counts and durations at concurrency 1 and at the winning concurrency. Missing or zero duration supplies no rate. |
| Time to first token (TTFT) | Measure on the client monotonic clock from request send to the first nonempty generated text or reasoning delta. Ignore headers, comments, role markers and usage-only frames. Report each sample and nearest-rank p50 and p95 with sample counts. |

SSE frames are transport units and may carry multiple tokens. Use actual token counters and retain any missing measurements as unavailable. Keep counters from the engine clock separate from the client clock. Partial, failed and inconclusive samples remain diagnostic. The throughput winner and highest demonstrated stable concurrency may differ.

## Recommend a profile and retain hardware records

A proposed TOML fragment preserves exact model and tier identity. Recommend slots at or below the demonstrated stable concurrency and the intended launch limits. A normal-use recommendation requires a supported tier of at least 96k nominal context whose usable envelope and separate long cell qualify at the intended concurrency, along with its short-grid evidence. Before measuring, record the operator's usable context goal, intended concurrency, minimum aggregate throughput and maximum TTFT for the workload. Prefer larger qualified windows when they serve that context goal and satisfy the throughput and TTFT thresholds. Show the tradeoffs in usable context, demonstrated slots, throughput and TTFT at the proposed concurrency. Explicit smaller tiers remain available where supported. Record failed and untested tiers with their respective evidence. Leave the normal-use recommendation unresolved if no tier fits the goals. The configured default stays in force until the operator separately adopts a reviewed change. Runtime admission still depends on live model-bound capacity and existing lifecycle authority. [Serving profiles](../SPEC.md#76-serving-profiles-and-context-window-escalation)

Save one immutable, versioned JSON record per run, including refusals and partial attempts. Use a new path and atomic write. Record physical RAM and swap in bytes, effective memory and swap limits and their provenance, CPU and effective limits, ordered assigned GPU IDs and VRAM, and effective runtime and launch settings. Preserve model, weights, quantization, tokenizer, template, executable and build identities. Retain workload version, generator identity and digest, seed, corpus and body digests, timers, nominal and usable context, collector/admission evidence, raw samples and statuses. Record OS and driver versions, power settings, thermal conditions and background load. Record combined RAM-plus-swap separately from a swap-only limit. Keep credentials, private paths and unrestricted command or environment dumps out of the record.

Match model weights and quantization, runtime build, tokenizer and template, workload and tier, generator version and digest, seed and corpus, effective launch recipe and limits, observed context, effective shared settings and sampler, cache and deadline policies, adapter semantics, and concurrency. Retain instance and run IDs as provenance outside the recipe match. Report changes in OS, driver, power, thermals and background load alongside the measurements. If a RAM upgrade also raised the container limit, report a deployment change. Even with equal effective limits, the delta is observational and may reflect other changes alongside physical RAM.

These illustrative short-grid records assume a hypothetical, separately supported and approved 96k launch with at least four configured and independently witnessed slots. The shipped HY4 sample declares one slot per tier and the current selector accepts only 32k. The two example records hold CPU, GPUs and effective launch settings equal, including a 54 GiB RAM limit and 2 GiB swap-only bound. [Shipped HY4 catalog](../samples/router.toml#L153-L164)

| Record | Physical RAM bytes | C=2 aggregate median | Highest demonstrated | Peak aggregate and winning concurrency |
| --- | ---: | ---: | --- | --- |
| 64 GB class | 68,719,476,736 | 8.0 tokens/s | 2, C=3 failed, C=4 untested | 8.0 tokens/s at C=2 |
| 128 GB class | 137,438,953,472 | 9.0 tokens/s | 4, search capped at 4 | 14.0 tokens/s at C=4 |

The illustrative matched C=2 observational delta is +1.0 tokens/s. The independent peak changes are useful alongside each record's tested range. A C=4 throughput delta is unavailable because the smaller host has no C=4 result. Actual records would carry decode, prefill and TTFT samples as well as the separate normal-use cell status.
