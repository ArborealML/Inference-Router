# Offline Docker selector coverage

The inspected reference selector accepted 169 of 506 explicit model/profile spelling combinations and refused 337. Its nine canonical models and thirteen model aliases resolve to 75 distinct model/profile targets. The inventory also checked 22 omitted-profile defaults, 12 spellings that require an explicit single GPU, and 1,248 valid parking parameter combinations. Local installer paths, deployment configuration and raw inventory are held outside the repository.

| Backend family | Normalized targets | Launch settings that still need representation or verification |
| --- | ---: | --- |
| Strata | 36 | The offline projection supports literal per-tier environment and data/model binds. The capped Qwen profiles use per-tier memory and total memory plus swap. The selector's `max-4` alias of `256k-4` covers six spellings across three existing targets and adds zero normalized targets. AIR still needs the alias mapping. |
| HyperQwen | 26 | Different profiles and options change the ordered GPU request and shared-memory limit. Parking derives shared memory from its 1–48 GiB RAM cache. Init, restart policy and log settings also need representation. Shared wire IDs need exact checkpoint and serving-instance proof. |
| Maya | 13 | MiniMax `32k` and all three GLM tiers have offline launch projections. The nine DeepSeek targets also require an explicit Docker probe timeout and retry count, plus nested bind destinations. |

The environment and bind projection merged in [PR #7](https://github.com/ArborealML/Inference-Router/pull/7). It composes settings for offline validation and keeps target-enabled startup and native Docker writes gated. [Configuration and lifecycle contract](../SPEC.md#7-configuration-toml)

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
