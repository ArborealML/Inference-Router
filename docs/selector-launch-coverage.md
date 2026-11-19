# Offline Docker selector coverage

The inspected reference selector accepted 169 of 506 explicit model/profile spelling combinations and refused 337. Its nine canonical models and thirteen model aliases resolve to 75 distinct model/profile targets. The inventory also checked 22 omitted-profile defaults, 12 spellings that require an explicit single GPU, and 1,248 valid parking parameter combinations. Local installer paths, deployment configuration and raw inventory are held outside the repository.

| Backend family | Normalized targets | Launch settings that still need representation or verification |
| --- | ---: | --- |
| Strata | 36 | Offline projection supports per-tier environment and data/model binds, memory plus swap for capped Qwen profiles, and the `max-4` alias for `256k-4`. |
| HyperQwen | 26 | Eight paired-GPU, six single-GPU and twelve default parking vectors have offline launch projections. Shared wire IDs need exact checkpoint and serving-instance proof. |
| Maya | 13 | MiniMax `32k`, the three GLM tiers and Maya-XS `32k` have qualified offline projections. Per-tier literal commands express the eight general DeepSeek launch payloads. |

The environment and bind projection merged in [PR #7](https://github.com/ArborealML/Inference-Router/pull/7). It composes settings for offline validation and keeps target-enabled startup and native Docker writes gated. [Configuration and lifecycle contract](../SPEC.md#7-configuration-toml)

## Strata profile alias

For Swift Flash Next, Qwen Flash Next and Qwen Flash Next Coder, declare `aliases = ["max-4"]`
on the canonical `256k-4` serving row. The alias covers six pairs across the models and their
model aliases. Each pair uses the same offline launch plan as its canonical spelling,
with 256,000 requested context tokens and four slots. Omitted tiers still resolve to
`max`, `512k` and `max` respectively. The inventory remains at 36 Strata targets and 75 overall.

## Strata memory boundary

| Qwen profile | Context and slots requested by the selector | Container memory | Container memory plus swap |
| --- | --- | --- | --- |
| `512k` (default) | 524,288 tokens, one slot | Omitted | Omitted |
| `768k` | 786,432 tokens, one slot | 57,982,058,496 bytes (54 GiB) | 62,277,025,792 bytes (58 GiB total) |
| `1m` | 1,048,576 tokens, one slot | 57,982,058,496 bytes (54 GiB) | 62,277,025,792 bytes (58 GiB total) |

Per-tier limits express the capped profiles while the default continues to omit both fields. The offline projection selects the whole limit pair for the chosen tier. These values describe selector launch inputs. Host capacity, canonical launch receipts, exact serving-instance identity and admission through the resolved route remain separate qualification requirements.

## MiniMax 32k launch options

For a dedicated MiniMax engine, offline projection sends `HostConfig.IpcMode = "host"`, the
one-executable `Entrypoint`, and the separate selector `Cmd`. The requested engine settings
are 32,768 tokens and one slot, with 54 GiB memory and 56 GiB total memory plus swap.
Either launch option prevents runtime startup and Docker mutation pending host qualification.

## GLM serving probes

GLM `32k`, `256k` and default `1m` each request one slot. Their Docker payloads share the
Python entrypoint, all-GPU request, 20 CPUs, 54 GiB memory and 56 GiB total memory plus swap.
The tier appears in the serving arguments and in the literal `CMD-SHELL` probe. Each probe
has a 30-second interval and a 3,600-second start period. Probe timeout, retries and startup
interval follow Docker inheritance and defaults. Omission of the restart policy preserves the
selector's disabled-restart behavior. The three projections establish launch intent only.

## DeepSeek Maya-XS 32k

The model's sole tier uses fixed decimal `32768` serving and probe arguments. A dedicated
engine can keep the existing literal command. The Docker payload requests a 10-second probe
timeout and three retries. Its 11 ordered bind mounts include two explicit read-only children
of `/work` and an independent writable log mount. The qualifier compares equivalent JSON and
TOML configurations with one redacted payload.

Maya-XS brought landed offline coverage to 41 of 75 targets. Effective read-only mount
enforcement and the AIR readiness budget require separate host qualification.

## General DeepSeek serving commands

One engine holds all eight canonical tiers. Each tier supplies a literal
`["/selector.py", "serve", "<decimal context>"]` argument array and a separate probe with the
same decimal. The `max` default passes `1048576` while the `32k` tier passes `32768`.
The independent payload check covers the nine ordered mounts, the existing loopback port,
the resource limits and the literal probe for every tier. The three selector model spellings
span 24 explicit spelling and tier pairs. The AIR fixture also checks the canonical model ID
at every tier, for 32 resolution checks per config format. The eight profiles brought landed
offline coverage to 49 of 75.

## HyperQwen paired-GPU container policy

One engine projects the `max`, `max-2`, `128k-2` and `32k` tiers for each of the
Swift and Classic checkpoints. These eight targets share the ordered GPU request, 2 GiB of
shared memory, a 60-second stop timeout and a loopback port binding. The `max` tier remains
the default in both catalogs. The `32k` payload requests four sequences per data-parallel
replica. The projection does not establish global admission capacity.

Docker init, `unless-stopped` restart policy and log rotation at `20m` with three files are
common to the eight payloads. The daemon selects its default log driver. An opted-in published
port includes its matching TCP exposure. The dual-format fixture uses distinct synthetic image
identity fields and compares both formats to one redacted full payload. Before activation,
verify that the daemon's effective log driver supports both rotation options and refuse if its
identity or option support is unknown or incompatible. Defiant's current image association, restart authority
and peer ownership require host verification. Swift and Classic share a wire model ID, so
activation also requires exact checkpoint and serving-instance binding.

The paired policy brought landed offline coverage to 57 of 75 targets.

## HyperQwen single-GPU tiers

Swift and Classic each have `single-64k`, `single-128k-2` and `single-max`. The selector
requires one explicit local device index, either 0 or 1 in the recorded inventory. The
tier overrides the entire common NVIDIA device request while the default `max` tier keeps
the ordered pair. Device choices and aliases do not increase the normalized target count.

All six single-GPU profiles pass the one-device tensor-parallel settings and retain 2 GiB
of shared memory. The `single-64k` profile sets `CTX=fast` and four draft tokens. The
other two use `CTX=long` and three draft tokens. Configured sequence counts describe
launch intent. Device-index mapping, toolkit compatibility, serving fit and host authority
require independent verification before activation.

The single-GPU slice brought landed offline coverage to 63 of 75 targets.

## HyperQwen parking shared memory

Swift and Classic each have six parking tiers. Their default vectors request 32 GiB of
application cache and 36 GiB of Docker shared memory, or 38,654,705,664 bytes. Ordinary
tiers inherit the common 2-GiB size. The selector's cache endpoints of 1 and 48 GiB map
to 5 and 52 GiB of Docker shared memory. The `parked` tier defaults to two sequences
and allows one through eight when re-resolved. The other five parking tiers keep their
named sequence counts. Other choices require separate configuration of the resolved vector.

The parking slice brought landed coverage to 75 of 75 normalized offline target defaults. This measures launch projection. Effective shared memory, available host RAM
of at least cache plus 10 GiB after the predecessor stops, device and logging compatibility,
readiness and host authority require independent verification before activation. The first
native swap pair remains Qwen and Swift on one host.

## Reproduce the offline comparison

The six redacted TOML specimens cover 75 default launch vectors across nine canonical
models. Each has one native router-owned engine. Four independently configured Maya
engines share one listener address, leaving three distinct backend addresses in the file
union. The sweep exercises the loader's refusal when those six definitions are put into
one native configuration. One combined native config and live selector-to-router
equivalence remain open.

The [fixture manifest](../tests/Arboreal.InferenceRouter.Tests/SelectorFixtures/manifest.json)
pins the six original specimen hashes, the redacted hashes in their committed LF form, the serving launcher source
hashes and the original receipt hashes. The [selector reference](../tests/Arboreal.InferenceRouter.Tests/SelectorFixtures/selector-reference.json)
contains the expected create document, accepted spellings, default, capacity and applicable
device choices for each row. Its expected values were transcribed from the original
records and serving branches. The verifier never uses the AIR specimens, production
serialization or another AIR golden to derive those expectations.

Run the cheap check used by default CI.

```sh
dotnet test -c Release --filter FullyQualifiedName~SelectorDifferentialTests.RedactedFixtureSmoke
```

Run the direct sweep as an explicit opt-in. Both the production loader and Docker
create projection run in-process. A throwing HTTP handler guards the provider and
catalog view. The driver calls neither container lifecycle nor serving endpoints.

```sh
ARBOREAL_RUN_SELECTOR_DIFFERENTIAL=1 dotnet test -c Release \
  --filter FullyQualifiedName~SelectorDifferentialTests.OfflineSelectorSweep \
  --logger 'console;verbosity=detailed'
```

The sweep requires 10,150 distinct full create comparisons. That count is 49
non-HyperQwen defaults, 2,496 parking and 28 ordinary HyperQwen vectors at each of four
utilization forms, three off-grid values across different shapes, and two in-range
float-neighbor cases. Devices are checked in both recorded orders for paired tiers and
on both recorded devices for single tiers. The four utilization forms are the omitted
literal `0.90` and explicit `0.5`, `0.9`, and `0.95`. Parking covers every integer cache
value from 1 to 48 GiB, and variable `parked` covers every sequence count from 1 through 8.
The five fixed-lane profiles retain their named counts. Each projection comes from a
native config resolved through the production loader. AIR takes explicit effective
settings in config, so different vectors require generating and loading different settings.

The driver reports the 169 explicit spelling probes, 22 omitted-profile probes, 78
format comparisons and the executed AIR refusals independently of that vector count.
It verifies actual catalog `MaxContext` and `MaxSlots` separately from the backend command
and environment. The HyperQwen DP2 `32k` declaration omits global `max_slots`, so AIR
loads a default of one while `MAX_SEQS=4` applies per replica. MiniMax uses the production
catalog lookup to resolve the original spelling and loaded `32k` default, then projects
both omitted and explicit profiles with the original native settings. The comparator
checks full property sets and preserves array order. A deliberate one-byte change to
expected parked `HostConfig.ShmSize` must identify that exact field.

### Reference provenance

A clone can check the redacted bytes and reproduce the AIR results. The named original
artifacts below are held by the coordinator with the private selector receipt. Verifying
their authenticity and the redaction against them requires access to those artifacts.

| Original record pinned in the manifest | What it supplies |
| --- | --- |
| `defiant-selector-matrix.json` | Model names, aliases, raw tier spellings and defaults, contexts, slots, source fingerprints, and archived option inventory |
| `defiant-native-coverage-3af5f10.json` | 75 normalized rows, exact Strata target environment and bind choices, memory and total memory-plus-swap, HyperQwen device choices and parking ranges |
| `defiant-launch-targets.json` | Strata launcher and adapter provenance, common resource and bind intent, and protected credential target |
| `selector-validator-receipt.json` | Original inventory census, including 337 rejected spelling pairs and 1,248 parking tuples before device expansion |

The serving rules used by the reference are summarized here with the source filenames
and branches. The manifest pins the original bytes for each file.

- `select_model.py`, model table, preflight and `gpu_utilization`. Resolve the spellings
  and defaults before launch. Parse explicit utilization as a float in the inclusive
  range 0.50 to 0.95, then pass its normalized string. The launcher supplies `0.90` on omission.
- `start-strata.sh`, serving `docker run` and resource branch, with profile sizing and
  tuning from `strata_profiles.py`. All GPUs, 2 GiB SHM, unlimited memlock, 120-second
  stop timeout, ordered data and model binds, and the model/profile environment. Qwen
  `768k` and `1m` require 54 GiB RAM and 58 GiB total RAM plus swap. The selector puts
  model mounts first and interleaves shared and profile environment entries.
- `minimax_selector.py`, `glm_selector.py`, and `deepseek_selector.py`, their serving
  `docker('run', …)` and `mounts` branches. All GPUs, host IPC, 20 CPUs, 54/56 GiB RAM and
  total RAM plus swap, 120-second stop timeout and the Python entrypoint. MiniMax
  serves `32k` without a Docker health command. GLM passes its tier text and uses
  30-second interval and 3600-second start period. DeepSeek passes decimal context
  and adds the 10-second timeout and three retries. Both health commands use
  `CMD-SHELL`. Maya-XS adds the two absolute-path mounts and environment to the
  ordered DeepSeek serving mounts before the separate writable log.
- `start-hyperqwen-profile.sh`, device/profile validation and cache-directory branches,
  plus `hyperqwen_parking.py` and the archived HyperQwen serving launcher. The
  launcher preserves GPU order, prepares a 2 GiB default SHM size or
  `(cacheGiB + 4) × 2³⁰` bytes when parking, and sets `KV_OFFLOAD_GB` and
  `MAX_SEQS` according to the tier. The `32k` branch uses data parallelism.
  The other branches set tensor parallelism, with `single-64k` selecting fast
  context and four draft tokens. Mount order, the two Swift tiers' shared tuning
  cache, the profile cache choices, environment order, init, restart and rotation
  settings are carried into the reference.

Inputs outside the utilization bounds, including `0.49999999999999994` and
`0.9500000000000001`, malformed floats, NaN and infinity, cache 0 or 49, sequence
counts 0 or 9, conflicting fixed lanes and invalid devices are selector source
cases. The clone run does not execute the installed selector parser or re-run the
337 archived rejections. Its negative operations exercise the AIR loader on a
duplicate GPU index, zero SHM, the protected mount and the combined config, and
exercise the AIR resolver on an unknown tier.

### Substitutions and payload differences

The specimens substitute inert install and HyperQwen mount prefixes and synthetic
credential, cache and adapter paths. In-image absolute bind aliases retain the same
value as their source where the selector requires it. Loopback ports are shifted
together, preserving listener sharing, and the router label is synthetic. Each
image family uses a distinct fixture-only locator, named repository digest and
daemon-local ID. The request's `Image` must be the local ID. The fixtures provide
zero evidence about any current host image association.

| Field | Difference represented in the reference |
| --- | --- |
| `Labels` | AIR adds canonical router/engine/model/profile labels. Selector launch and signature labels are absent. HyperQwen's selector signature varies with the vector and active artifacts. |
| `Env`, `HostConfig.Mounts` (Strata) | Expected AIR order is common then tier. The selector's order differs. The protected `/run` credential target is refused. The projected substitute path and its environment value require pinned-image verification. |
| `ExposedPorts` (Strata, Maya) | The selector CLI publishes the TCP port and emits root exposure. AIR projects the matching `PortBindings` with root exposure absent. Any inherited exposure needs pinned-image evidence. |
| `HostConfig.RestartPolicy` (Strata, Maya) | AIR omits the Docker default. The selector passes explicit `--restart no`. |
| `HostConfig.LogConfig` (HyperQwen) | AIR leaves the driver empty and requests both rotation options. Effective driver identity and support still require verification. |

Host checks still include exact image, checkpoint and instance binding, physical device
and toolkit fit, effective mounts and SHM, available RAM after stop, restart and logging,
serving readiness, fenced ownership and route-specific admission. Config loads and create
projections establish configured launch intent.
