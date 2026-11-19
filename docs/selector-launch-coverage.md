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

Qualification and landing would bring coverage to 75 of 75 normalized offline target
defaults. This measures launch projection. Effective shared memory, available host RAM
of at least cache plus 10 GiB after the predecessor stops, device and logging compatibility,
readiness and host authority require independent verification before activation. The first
native swap pair remains Qwen and Swift on one host.
