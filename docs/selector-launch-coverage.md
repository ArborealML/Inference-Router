# Offline Docker selector coverage

The inspected reference selector accepted 169 of 506 explicit model/profile spelling combinations and refused 337. Its nine canonical models and thirteen model aliases resolve to 75 distinct model/profile targets. The inventory also checked 22 omitted-profile defaults, 12 spellings that require an explicit single GPU, and 1,248 valid parking parameter combinations. Local installer paths, deployment configuration and raw inventory are held outside the repository.

| Backend family | Normalized targets | Launch settings that still need representation or verification |
| --- | ---: | --- |
| Strata | 36 | The offline projection supports literal per-tier environment and data/model binds. The capped Qwen profiles use per-tier memory and total memory plus swap. The `max-4` profile spelling maps to `256k-4` in the selector and still needs a canonical profile-alias representation in AIR. |
| HyperQwen | 26 | Different profiles and options change the ordered GPU request and shared-memory limit. Parking derives shared memory from its 1–48 GiB RAM cache. Init, restart policy and log settings also need representation. Shared wire IDs need exact checkpoint and serving-instance proof. |
| Maya | 13 | MiniMax `32k` is the first target in the installed selector order. It requires host IPC and an explicit entrypoint. GLM and DeepSeek also use Docker health checks, and DeepSeek uses nested bind destinations. |

The environment and bind projection merged in [PR #7](https://github.com/ArborealML/Inference-Router/pull/7). It composes settings for offline validation and keeps target-enabled startup and native Docker writes gated. [Configuration and lifecycle contract](../SPEC.md#7-configuration-toml)

## Strata memory boundary

| Qwen profile | Context and slots requested by the selector | Container memory | Container memory plus swap |
| --- | --- | --- | --- |
| `512k` (default) | 524,288 tokens, one slot | Omitted | Omitted |
| `768k` | 786,432 tokens, one slot | 57,982,058,496 bytes (54 GiB) | 62,277,025,792 bytes (58 GiB total) |
| `1m` | 1,048,576 tokens, one slot | 57,982,058,496 bytes (54 GiB) | 62,277,025,792 bytes (58 GiB total) |

Per-tier limits express the capped profiles while the default continues to omit both fields. The offline projection selects the whole limit pair for the chosen tier. These values describe selector launch inputs. Host capacity, canonical launch receipts, exact serving-instance identity and admission through the resolved route remain separate qualification requirements.
