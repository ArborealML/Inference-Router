# Use AIR from Pi CLM

With Pi 1.3 and an existing Pi CLM installation, install the extension beside the launcher. The installer recognizes the launcher that ships an `air_provider.py` helper. It checks the launcher layout before replacing files. For upgrades, run the same installer and use `/reload` in the open session. The installer embeds the catalog helper in the extension entry so the running Pi process picks up both together.

```sh
python3 tools/pi-air/install.py /path/to/pi-clm.py
AIR_CONFIG=/path/to/router.toml pi-clm
```

The launcher reads the local AIR listener from the configuration. Set `AIR_API_KEY` when that listener requires authentication. The existing legacy backend discovery still runs when the launcher has no AIR configuration. Existing Pi flags, session directory, tools and CLM extensions are available in both modes.

- **Select a model** with Pi’s built-in `/model`. Both loaded and cold configured models appear. A fresh session prefers a healthy resident. If none is resident it starts on the configured default when available. Pi restores its saved model on resume. Catalog and engine notifications update the picker while the session is open. Reconnection and `/reload` fetch the current snapshot.
- **Inspect context** with `/context`. The picker labels each configured profile with its capacity and slot count by engine, alongside observed residency. Use `/context NAME` to choose a profile or `/context default` to use the configured default. RPC clients can send these commands through `prompt`. Choices follow the current session branch and model. Selecting a profile configures the next inference request. AIR performs any necessary backend switch during admission.
- **Fit the transcript** before downsizing. Pi estimates the current context and includes the output reserve. If it exceeds the new window, use `/compact` while on the larger profile, then repeat the selection. When multiple engines advertise a profile name, Pi budgets to the smallest advertised capacity for that name. A withdrawn selection requires a new choice.
- **Follow a request** in the Pi footer or with `/air-status`. The extension reads the ticket returned by the original inference response. While the displayed ticket is queued, the composer shows **Waiting**. Dispatch restores Pi’s usual working indicator. The footer clears when the stream ends. Pi shows the result in the transcript, and the command can read the latest ticket on this branch after completion. `/air-cancel` aborts this session’s active inference connection. If several calls overlap on one branch, run `/air-cancel` to list their IDs and use `/air-cancel ID` for the intended call. Pi’s ordinary abort also propagates to AIR.

Live Pi qualification verified 32k and 256k inference. With two requests in flight, cancelling the selected ticket left the other request running through completion.

The extension uses read-only admin discovery and ticket snapshots. Pi streams the original response through its OpenAI adapter. AIR launcher settings disable automatic transport and agent-error retries. The client sends its session ID as `X-Inference-Thread`, the chosen capacity as `X-Inference-Required-Context`, and an exact `X-Inference-Profile` when a named profile applies.

Run the catalog checks with `node --test tools/pi-air/catalog.test.mjs`. With Pi 1.3 on the path, `python3 tools/pi-air/test_prompt_budget.py` verifies a same-session extension upgrade and that a new prompt exceeding the selected window reaches the user as an error before an inference or automatic compaction request reaches the loopback fixture.
