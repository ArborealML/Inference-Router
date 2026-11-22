# Local queue-driven model switching

Use native Docker lifecycle with `lifecycle_owner = "router"` and a named
`lifecycle_group` on each managed engine. Put engines sharing exclusive GPUs in
the same group. Independent groups can progress concurrently. Groups, engines,
models and clients have no fixed two-member limit.

Reuse the existing Docker daemon, credentials and configured catalog. Each engine
needs its own endpoint and pinned image identity. Members of one group use the
same daemon and router identity. The router stops only containers bearing its
matching ownership labels; an existing unowned resident requires an explicit
operator cutover. This mode does not exclude unrelated GPU processes or other
Docker clients.

A cold request waits in the ordinary queue while active transport uses drain.
The original request then owns launch and readiness, including its existing wait
budget. Cancellation before selection does not launch its target. A detached
transport still holds its charge until cleanup completes. After a failed switch, each later request checks the outstanding operation and the
current inventory of every group member. AIR can verify and reuse one running resident,
switch it out, or launch when the resources are vacant. An unresolved write or ambiguous
ownership fails that request with `switch_failed`. A later request checks again. AIR never
replays the failed inference. Authenticated idle reload can add engines to an existing group and aliases to its models. Direct admin switching remains disabled for group-bound engines.

The default Docker API call budget is five minutes (`call_timeout_ms = 300000`).
Docker's default graceful stop is 30 seconds (`stop_timeout_ms = 30000`), and
AIR's default readiness wait after launch is ten minutes (`wait_timeout_ms = 600000`).
An explicit value in the engine configuration takes precedence for its own setting.

Configure loaded-status filtering and backend health/context/slot probes for the
chosen catalog. Native readiness retries temporary listener failures within its
original deadline and checks the exact container between unsuccessful rounds and
before publishing readiness. It refuses an exited or replaced container.

For Python backends using `serve.server`, the
[startup wrapper](../tools/local-docker/README.md) automatically creates configured
log, temporary and slot directories as the existing container user before launching
the module. No manual nested log-directory preparation is required.

Pi's AIR provider must list the models from the configured catalog. Model selection
changes the model on ordinary completion requests; no admin switch is needed.

## Verification

A live Pi run selected Qwen and queued a DeepSeek request while the first request
was active. Qwen streamed `READY` and completed. The same queued DeepSeek ticket
then drove the physical switch and streamed `SECOND_READY`. Both clients had
retries disabled. Docker showed the Qwen container stopped successfully and Maya
running; authenticated backend readback reported the selected DeepSeek model,
32,768 context tokens and one slot. Its native process was resident on the GPUs.
The detailed host transcript is held outside the repository.

A separate live Pi request exercised recovery after router restart. With a changed Docker
restart policy on the owned Maya container, the original request triggered one stop, one
create and one start, then streamed its reply. Backend readback confirmed DeepSeek at
32,768 context tokens and one slot, with its replacement worker on both GPUs.

Focused tests also exercise three shared engines, a fourth independent engine,
queued cancellation and transport cleanup after a timeout. Native AOT publishing
completed without warnings. The live pair does not restrict the configured path.
