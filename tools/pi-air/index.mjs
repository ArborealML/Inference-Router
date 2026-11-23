import { estimateTokens, getPackageDir, SettingsManager, shouldCompact } from "@earendil-works/pi-coding-agent";
import { openAICompletionsApi } from "@earendil-works/pi-ai/compat";
import { randomUUID } from "node:crypto";
import { join } from "node:path";
import { setTimeout as delay } from "node:timers/promises";
import { pathToFileURL } from "node:url";

import { contextFits, observedWindow, outputBudget, parseCatalog, piModel, requestFits, tier } from "./catalog.mjs";

class PermanentTicketError extends Error {}

const entryType = "air-context";
const ticketStates = new Set(["Queued", "WaitingForModel", "Executing", "Completed", "Cancelled", "Failed"]);
const clean = value => String(value ?? "").replace(/[\x00-\x1f\x7f-\x9f]/g, " ").slice(0, 450);

// Request-scoped counters only. A receiver-side text estimate is explicitly marked approximate.
function observeSpeed(req, chunk, now) {
    const progress = chunk?.prompt_progress;
    const timings = chunk?.timings;
    let prefill = timings?.prompt_per_second;
    if (!(prefill > 0) && Number.isSafeInteger(progress?.processed) && Number.isSafeInteger(progress.cache) &&
        progress.cache >= 0 && progress.processed <= progress.total && progress.time_ms > 0)
        prefill = (progress.processed - progress.cache) * 1000 / progress.time_ms;
    const previousPrefill = req.prefill, previousDecode = req.decode;
    if (Number.isFinite(prefill) && prefill > 0) req.prefill = prefill.toFixed(1);

    const previousPercent = req.percent, previousPhase = req.phase;
    const delta = chunk?.choices?.[0]?.delta;
    if (delta?.tool_calls?.length) req.phase = "Preparing Tool";
    else if (delta?.content) req.phase = "Responding";
    else if (delta?.reasoning_content || delta?.reasoning || delta?.reasoning_text) req.phase = "Reasoning";
    else if (Number.isSafeInteger(progress?.processed) && Number.isSafeInteger(progress.total) &&
             progress.total > 0 && progress.processed >= 0 && progress.processed <= progress.total) {
        req.phase = "Reading Context";
        req.percent = Math.floor(100 * progress.processed / progress.total);
    }
    if (req.phase && req.phase !== "Reading Context" && req.percent !== undefined) req.percent = 100;
    const chars = (delta?.reasoning_content?.length ?? 0) + (delta?.content?.length ?? 0) +
        (delta?.tool_calls ?? []).reduce((sum, call) => sum + (call.function?.arguments?.length ?? 0), 0);
    const tokens = chunk?.usage?.completion_tokens;
    if (Number.isSafeInteger(tokens) && tokens > 0 && req.firstCount === undefined) {
        req.firstCount = tokens;
        req.firstCountAt = now;
    }
    if (chars && req.firstOutputAt === undefined) {
        req.firstOutputAt = now;
        req.charsSinceFirst = 0;
    } else if (chars) req.charsSinceFirst += chars;
    const decode = timings?.predicted_per_second;
    if (Number.isFinite(decode) && decode > 0) req.decode = decode.toFixed(1);
    else if (Number.isSafeInteger(tokens) && tokens > req.firstCount && now - req.firstCountAt >= 500)
        req.decode = ((tokens - req.firstCount) * 1000 / (now - req.firstCountAt)).toFixed(1);
    else if (chars && now - req.firstOutputAt >= 500 && req.firstCount === undefined)
        req.decode = "~" + (req.charsSinceFirst * 250 / (now - req.firstOutputAt)).toFixed(1);
    return previousPrefill !== req.prefill || previousDecode !== req.decode ||
        previousPercent !== req.percent || previousPhase !== req.phase;
}

export default async function air(pi) {
    const base = process.env.PI_AIR_BASE_URL;
    if (!base) return;
    const url = new URL(base);
    if (url.origin + "/v1" !== base || url.protocol !== "http:" || url.hostname !== "127.0.0.1") {
        throw new Error("Invalid local AIR listener");
    }
    const auth = process.env.AIR_API_KEY;
    const adapter = openAICompletionsApi();
    let catalog, etag, ctx, lifetime, branch, latest, turnSelection;
    let engines = [];
    let choices = new Map();
    let budgetRefused = false;
    let workingLabel;
    const runningTools = new Set();
    let speedRequest;
    const active = new Set();
    const agentRequestHeaders = new WeakSet();
    // CLM can replace the system message while preserving the rest of Pi's prepared turn.
    let preparedMessages;
    const summaries = new WeakMap();
    const currentRequests = () => Array.from(active).filter(req => req.branch === branch);
    const requestKey = req => req.ticket || req.clientId;
    const matchesRequest = (req, id) => req && (req.ticket === id || req.clientId === id);
    function updateTicketUI() {
        const current = currentRequests().at(-1);
        const waiting = current?.queued === true && !current.signal.aborted;
        const phase = runningTools.size ? "Executing Tool" : current === speedRequest && current &&
            !current.signal.aborted && !waiting && !["Completed", "Cancelled", "Failed"].includes(current.lastState)
            ? current.phase : undefined;
        const status = phase ? snapshotText(null, {State: phase, Model: current?.model}) :
            current?.observationWarning ?? current?.text;
        const rates = speedRequest && `prefill ${speedRequest.prefill ?? "—"}${speedRequest.percent === undefined ? "" : ` (${speedRequest.percent}%)`} · decode ${speedRequest.decode ?? "—"} tok/s`;
        ctx?.ui.setStatus("air-ticket", [rates, status].filter(Boolean).join(" · ") || undefined);
        const label = waiting ? "Waiting" : phase;
        if (label !== workingLabel) {
            ctx?.ui.setWorkingMessage(label);
            workingLabel = label;
        }
    }
    const admin = (path, options = {}) => fetch(url.origin + "/admin/" + path, {
        ...options, redirect: "error", headers: {...(auth ? {Authorization: "Bearer " + auth} : {}), ...options.headers}
    });
    const boundedSignal = signal => signal ? AbortSignal.any([signal, AbortSignal.timeout(6000)]) : AbortSignal.timeout(6000);
    const notify = (message, level = "info") => ctx?.ui.notify(clean(message), level);
    function projectedTokens(context) {
        const {messages} = context.sessionManager.buildSessionProjection();
        return messages.reduce((total, message) => total + estimateTokens(message), 0);
    }
    const selected = id => choices.get(id) || null;

    function resolveSelection(model, preference) {
        const row = catalog.models.get(model.id);
        const profile = row && tier(row, preference);
        const capacity = profile && (profile.window || observedWindow(row, engines));
        return {model, profile, capacity, maxTokens: capacity && outputBudget(capacity)};
    }

    async function refresh(signal) {
        const options = {signal: boundedSignal(signal)};
        const result = await admin("catalog", {...options, headers: etag ? {"If-None-Match": etag} : {}});
        let next = catalog;
        if (result.status !== 304) {
            if (!result.ok) throw new Error("AIR catalog HTTP " + result.status);
            next = parseCatalog(await result.json());
        }
        let live = [];
        try {
            const status = await admin("status", {signal: boundedSignal(signal)});
            if (status.ok) live = (await status.json()).Engines;
        } catch { /* Catalog entries remain usable without observed residency. */ }
        signal?.throwIfAborted();
        const changed = next !== catalog || JSON.stringify(live) !== JSON.stringify(engines);
        catalog = next;
        if (result.status !== 304) etag = result.headers.get("etag");
        engines = live;
        if (changed) await publish();
    }

    function definition(row) {
        return piModel(row, selected(row.id), engines);
    }

    async function publish() {
        const models = Array.from(catalog.models.values(), definition);
        pi.registerProvider("air", {baseUrl: base, apiKey: auth ? "$AIR_API_KEY" : "air-local-noauth",
            authHeader: !!auth, api: "openai-completions", models, streamSimple});
        const current = ctx?.model;
        if (current?.provider !== "air" || !ctx.isIdle()) return;
        const updated = ctx.modelRegistry.find("air", current.id);
        if (!updated || !catalog.models.has(current.id)) {
            notify("AIR removed " + current.id + ". Choose an available model with /model.", "warning");
        } else if (selected(current.id) && !tier(catalog.models.get(current.id), selected(current.id))) {
            notify("AIR removed the selected profile " + selected(current.id) + ". Choose /context default or another profile.", "warning");
        } else if (updated.contextWindow !== current.contextWindow || updated.maxTokens !== current.maxTokens) {
            if (!contextFits(ctx, updated.contextWindow, current.contextWindow, () => projectedTokens(ctx))) {
                notify("AIR reduced the window below this transcript’s budget. Select a larger profile or compact first.", "warning");
                return;
            }
            await pi.setModel(updated);
        }
    }

    function snapshotText(ticket, state) {
        return ["AIR", ticket, state.State, state.Model, state.Engine, state.ErrorCode, state.Error].filter(Boolean).map(clean).join(" · ");
    }

    async function inspect(req, signal) {
        const response = await admin("tickets/" + encodeURIComponent(req.ticket), {signal: boundedSignal(signal)});
        if (!response.ok) {
            const message = "Ticket HTTP " + response.status;
            if (response.status >= 500 || response.status === 429 || response.status === 408) throw new Error(message);
            throw new PermanentTicketError(message);
        }
        const state = await response.json();
        if (state?.Id !== req.ticket || state.ThreadId !== req.thread) throw new PermanentTicketError("Ticket identity mismatch");
        if (!ticketStates.has(state.State)) throw new PermanentTicketError("Unrecognized ticket state");
        if (req.branch === branch) {
            // A manual status read and the poll may return out of order. A dispatched ticket never requeues.
            if (req.dispatched && state.State === "Queued") return false;
            req.lastState = state.State;
            req.missedPolls = 0;
            req.observationWarning = undefined;
            req.queued = state.State === "Queued";
            req.dispatched = !req.queued;
            req.text = snapshotText(null, state);
            if (currentRequests().at(-1) === req) updateTicketUI();
            if (state.State === "Failed" && state.ErrorCode && !req.reported) {
                req.reported = true;
                notify(req.text, "error");
            }
        }
        return ["Completed", "Failed", "Cancelled"].includes(state.State);
    }

    async function watchTicket(req) {
        const pollIntervalMs = 700;
        let intervalMs = pollIntervalMs;
        while (!req.watch.signal.aborted) {
            try {
                if (await inspect(req, req.watch.signal)) return;
                intervalMs = pollIntervalMs;
            } catch (error) {
                if (req.watch.signal.aborted || req.branch !== branch) return;
                // Failed reads do not change the last confirmed ticket state.
                if (error instanceof PermanentTicketError) {
                    req.observationWarning = req.text + " · status unavailable (" + clean(error.message) + ")";
                    updateTicketUI();
                    return;
                }
                req.missedPolls = (req.missedPolls ?? 0) + 1;
                if (req.missedPolls === 4) {
                    req.observationWarning = snapshotText(null, {
                        State: "status unavailable (last known " + (req.lastState ?? "admitted") + ")", Model: req.model
                    });
                    updateTicketUI();
                }
                intervalMs = Math.min(intervalMs * 2, 4 * pollIntervalMs);
            }
            await delay(intervalMs, undefined, {signal: req.watch.signal}).catch(() => {});
        }
    }

    function streamSimple(model, transcript, options = {}) {
        const sessionId = ctx?.sessionManager.getSessionId();
        const sameSession = !!branch && options.sessionId === sessionId;
        // Pi's SDK passes the header object from its extension hook to the provider.
        // Direct registry calls merge their own headers, even when reusing loop options.
        const mainRequest = sameSession && options.signal && options.signal === ctx.signal &&
            agentRequestHeaders.delete(options.headers) && preparedMessages && transcript.messages.some(message => preparedMessages.has(message));
        if (mainRequest) preparedMessages = undefined;
        const summary = summaries.get(options.signal);
        const requestBranch = mainRequest ? branch : summary?.branch;
        if (requestBranch && requestBranch !== branch) throw new Error("AIR session branch changed before dispatch.");
        const thread = options.sessionId;
        if (!thread) throw new Error("AIR needs a Pi session identity");
        // A child can inherit this callback from its parent without loading the extension.
        const choice = sameSession || requestBranch ? selected(model.id) : null;
        const captured = mainRequest ? turnSelection : undefined;
        const selection = captured ?? summary?.selection ?? resolveSelection(model, choice);
        const {model: requestModel, profile: target, capacity, maxTokens} = selection;
        if (!target) throw new Error("AIR selection was removed. Choose an available /model or /context default.");
        if (!capacity) throw new Error("AIR has no configured or observed context budget for " + requestModel.id);
        // The new prompt and tool definitions can arrive after /context validated the prior turn.
        // Compaction and other auxiliary calls may have their own, shorter transcript.
        const estimatedInput = transcript.messages.reduce((total, message) => total + estimateTokens(message), 0);
        const sessionTokens = mainRequest ? ctx.getContextUsage()?.tokens : undefined;
        // Pi's agent loop carries defaults in its model, while maxTokens is a request limit.
        const reserve = Math.min(maxTokens, options.maxTokens ?? maxTokens);
        if (!requestFits(capacity, Math.max(estimatedInput, sessionTokens ?? 0), reserve)) {
            if (mainRequest) budgetRefused = true;
            throw new Error("The estimated AIR prompt and output reserve exceed this window. Select a larger /context or use /compact.");
        }
        if (mainRequest) budgetRefused = false;
        let req;
        if (requestBranch) {
            req = {thread, model: requestModel.id, branch, clientId: "request-" + randomUUID(), watch: new AbortController(), abort: new AbortController()};
            req.signal = options.signal ? AbortSignal.any([options.signal, req.abort.signal]) : req.abort.signal;
            active.add(req);
            latest = req;
            if (mainRequest) speedRequest = req;
            req.text = snapshotText(null, {State: "submitting", Model: requestModel.id});
            updateTicketUI();
        }
        const underlying = options.fetch ?? fetch;
        let sent = false;
        const original = adapter.streamSimple({...requestModel, contextWindow: capacity, maxTokens}, transcript, {
            ...options, signal: req?.signal ?? options.signal, maxTokens: reserve, maxRetries: 0,
            onPayload: async (payload, providerModel) => {
                const body = await options.onPayload?.(payload, providerModel) ?? payload;
                if (!mainRequest) return body;
                const hosts = [...(catalog.models.get(requestModel.id)?.engines ?? [])];
                if (hosts.length && hosts.every(name => ["hyper_classic", "hyper_swift"].includes(name)))
                    return {...body, stream_options: {...body.stream_options, include_usage: true, continuous_usage_stats: true}};
                if (hosts.length && hosts.every(name => ["strata", "strata_swift", "strata_coder", "maya", "maya_xs",
                    "glm", "minimax", "hy4"].includes(name)))
                    return {...body, return_progress: true, timings_per_token: true};
                return body;
            },
            onProviderStreamEvent: async (chunk, streamModel) => {
                if (mainRequest && req === speedRequest && req.branch === branch && !req.signal.aborted &&
                    observeSpeed(req, chunk, performance.now())) {
                    if (req.phase) {
                        req.queued = false;
                        req.dispatched = true;
                    }
                    updateTicketUI();
                }
                return options.onProviderStreamEvent?.(chunk, streamModel);
            },
            fetch: async (input, init) => {
                if (sent) throw new Error("AIR already received this Pi request");
                if (String(input) !== base + "/chat/completions" || init?.method?.toUpperCase() !== "POST")
                    throw new Error("Unexpected AIR inference destination");
                const liveRow = catalog.models.get(requestModel.id);
                const liveTier = liveRow && tier(liveRow, target.name);
                if ((requestBranch && requestBranch !== branch) || !liveTier || liveTier.name !== target.name ||
                    (liveTier.window || observedWindow(liveRow, engines)) !== capacity)
                    throw new Error("AIR cannot serve the selected model or profile as captured. Check /context.");
                const headers = new Headers(init.headers);
                headers.set("X-Inference-Thread", thread);
                if (target.name) headers.set("X-Inference-Profile", target.name);
                else headers.delete("X-Inference-Profile");
                headers.set("X-Inference-Required-Context", String(capacity));
                if (!auth) headers.delete("authorization");
                sent = true;
                const response = await underlying(input, {...init, headers, redirect: "error"});
                if (!req) return response;
                req.ticket = response.headers.get("X-Inference-Ticket");
                if (req.ticket && req.branch === branch) {
                    req.text = snapshotText(null, {State: "admitted"});
                    updateTicketUI();
                    void watchTicket(req);
                }
                return response;
            }
        });
        if (!req) return original;
        const onAbort = () => { if (req.branch === branch) updateTicketUI(); };
        req.signal.addEventListener("abort", onAbort, {once: true});
        void original.result().finally(() => req.signal.removeEventListener("abort", onAbort)).then(async message => {
            req.watch.abort();
            active.delete(req);
            if (req.branch !== branch) return;
            updateTicketUI();
            if (req.ticket) await inspect(req, req.branch.signal).catch(() => {});
            if (message.stopReason === "error" && req.lastState !== "Cancelled" && req.branch === branch && !req.reported)
                notify(snapshotText(req.ticket, {State: "Failed", Error: message.errorMessage}), "error");
        }).catch(() => {
            req.watch.abort();
            active.delete(req);
            if (req.branch === branch) updateTicketUI();
        });
        return original;
    }

    async function follow(signal) {
        const kinds = ["catalog.changed", "engine.switch_started", "engine.switch_completed",
            "engine.switch_failed", "engine.health", "engine.profile", "engine.registered"];
        while (!signal.aborted) {
            const connection = new AbortController();
            const observing = AbortSignal.any([connection.signal, signal]);
            let dirty = false, running, timer;
            const reconcile = async () => {
                while (dirty && !observing.aborted) {
                    dirty = false;
                    await refresh(observing);
                }
            };
            function schedule() {
                if (observing.aborted) return;
                dirty = true;
                if (timer || running) return;
                timer = setTimeout(() => {
                    timer = undefined;
                    running = reconcile().catch(() => connection.abort()).finally(() => {
                        running = undefined;
                        if (dirty) schedule();
                    });
                }, 100);
            }
            try {
                // AIR clamps a future cursor to the ring head at subscribe time.
                const feed = await admin("events?kinds=" + kinds.join(",") + "&since=9223372036854775807", {signal: observing});
                if (!feed.ok || !feed.body) throw new Error("AIR catalog events unavailable");
                await refresh(observing);
                let buffer = "";
                for await (const chunk of feed.body.pipeThrough(new TextDecoderStream())) {
                    buffer += chunk;
                    let newline;
                    while ((newline = buffer.indexOf("\n")) >= 0) {
                        const line = buffer.slice(0, newline).trimEnd();
                        buffer = buffer.slice(newline + 1);
                        if (line.startsWith("data:") && kinds.includes(JSON.parse(line.slice(5)).Kind)) schedule();
                    }
                    if (buffer.length > 128 * 1024) throw new Error("Invalid AIR catalog event");
                }
            } catch { /* Reconcile the current snapshot on reconnect, including after router restarts. */ }
            finally {
                connection.abort();
                clearTimeout(timer);
                await running;
            }
            await delay(1500, undefined, {signal}).catch(() => {});
        }
    }

    async function bind(context) {
        branch?.abort();
        for (const req of currentRequests()) { req.abort.abort(); req.watch.abort(); }
        branch = new AbortController();
        runningTools.clear();
        latest = speedRequest = undefined;
        ctx = context;
        updateTicketUI();
        budgetRefused = false;
        turnSelection = undefined;
        choices = new Map();
        for (const entry of ctx.sessionManager.getBranch()) {
            if (entry.type === "custom" && entry.customType === entryType)
                choices.set(entry.data.model, entry.data.profile);
        }
        await publish();
    }
    pi.on("session_start", async (_event, context) => {
        lifetime?.abort();
        lifetime = new AbortController();
        await bind(context);
        void follow(lifetime.signal);
    });
    pi.on("session_tree", async (_event, context) => bind(context));
    pi.on("session_shutdown", () => {
        lifetime?.abort();
        for (const req of active) { req.abort.abort(); req.watch.abort(); }
        branch?.abort();
        ctx?.ui.setStatus("air-ticket", undefined);
        if (workingLabel) ctx?.ui.setWorkingMessage(undefined);
        runningTools.clear();
        workingLabel = undefined;
        ctx = lifetime = branch = latest = speedRequest = turnSelection = undefined;
    });
    pi.on("message_start", (event, context) => {
        if (event.message.role !== "user") return;
        const model = context.model;
        turnSelection = model?.provider === "air" ? resolveSelection(model, selected(model.id)) : undefined;
        if (turnSelection?.profile?.name === null && turnSelection.capacity)
            notify("AIR could not resolve one serving profile for " + model.id +
                ". This turn uses automatic routing with a " + turnSelection.capacity + "-token minimum.", "warning");
    });
    pi.on("tool_execution_start", event => {
        if (turnSelection && branch && !branch.signal.aborted) {
            runningTools.add(event.toolCallId);
            updateTicketUI();
        }
    });
    pi.on("tool_execution_end", event => {
        if (runningTools.delete(event.toolCallId)) updateTicketUI();
    });
    pi.on("agent_settled", async () => {
        if (runningTools.size) {
            runningTools.clear();
            updateTicketUI();
        }
        turnSelection = undefined;
        if (ctx) await publish();
    });
    pi.on("context_with_system", event => { preparedMessages = new WeakSet(event.messages); });
    pi.on("before_provider_headers", event => { agentRequestHeaders.add(event.headers); });
    pi.on("cache_warming_decision", (_event, context) => {
        if (context.model?.provider === "air") return {action: "stop"};
    });
    pi.on("session_before_compact", async (event, context) => {
        const originBranch = branch;
        const pending = context.model;
        if (pending?.provider === "air" && (event.willRetry || (budgetRefused && event.reason !== "manual"))) {
            notify("AIR request ended. Use /compact or select a larger profile before submitting again.", "warning");
            return {cancel: true};
        }
        const selection = event.reason === "manual" ? undefined : turnSelection;
        if (selection) {
            if (pending?.provider !== "air" || !selection.capacity) return {cancel: true};
            const {preparation} = event;
            const settings = selection.model.id === pending.id ? preparation.settings :
                SettingsManager.inMemory(pi.getSettings()).getCompactionSettings(selection.model);
            // Pi checked the pending window before dispatching any queued user message.
            if (event.reason === "threshold" &&
                (selection.model.id !== pending.id || selection.capacity !== pending.contextWindow) &&
                !shouldCompact(preparation.tokensBefore, selection.capacity, settings)) return {cancel: true};
            if (preparation.settings.keepRecentTokens !== settings.keepRecentTokens) {
                try {
                    // Pi 1.3 ships the projected preparation helper outside its public barrel.
                    const helperUrl = pathToFileURL(join(getPackageDir(), "dist", "core", "compaction", "compaction.js"));
                    const {prepareCompaction} = await import(helperUrl.href);
                    if (event.signal.aborted || originBranch !== branch) return {cancel: true};
                    const replacement = prepareCompaction(event.branchEntries, settings);
                    if (!replacement) return {cancel: true};
                    // Pi uses this same preparation for its built-in summary and retry path.
                    Object.assign(preparation, replacement);
                } catch {
                    if (!event.signal.aborted && originBranch === branch)
                        notify("AIR could not apply the active model's compaction retention with this Pi installation.", "warning");
                    return {cancel: true};
                }
            }
            preparation.settings = settings;
        }
        if (pending?.provider === "air" && originBranch) summaries.set(event.signal, {branch: originBranch, selection});
    });
    pi.on("session_before_tree", (event, context) => {
        if (context.model?.provider === "air" && branch) summaries.set(event.signal, {branch});
    });
    pi.registerCommand("context", {description: "Show or select this AIR model’s context profile",
        handler: async (args, context) => {
            const originBranch = branch;
            const model = context.model;
            const row = model?.provider === "air" && catalog.models.get(model.id);
            if (!row) return notify("Select an available AIR model first.", "warning");
            const observed = engines.filter(e => e.Healthy && e.LoadedModels.includes(row.id)).map(e =>
                e.Engine + ": " + (e.ActiveProfile || "unknown profile") + ", " + (e.ActiveContextWindow || "unknown") +
                " tokens, " + (e.ActiveSlots || "unknown") + " slots");
            const status = observed.length ? "observed " + observed.join("; ") : "configured, no observed residency";
            const options = ["default", ...row.profiles.keys()];
            const summary = options.map(name => {
                const t = tier(row, name === "default" ? null : name);
                const offers = row.profiles.get(t.name)?.map(b => b.engine + " " + b.window + " tokens/" + b.slots + " slots");
                return name + " (" + (offers?.join(", ") || (t.window || observedWindow(row, engines) || "unknown") + " tokens") + ")";
            });
            const choice = selected(row.id) || "default";
            let name = args.trim();
            if (!name) {
                if (context.mode !== "tui") return notify(row.id + " · selected " + choice + " · " + status + " · configured " + summary.join(", "));
                const picked = await context.ui.select(row.id + " · selected " + choice + " · " + status, summary);
                if (!picked) return;
                name = options[summary.indexOf(picked)];
            }
            if (!options.includes(name)) return notify("Unknown AIR profile. Available: " + summary.join(", "), "warning");
            if (originBranch !== branch || ctx?.model?.provider !== "air" ||
                ctx.model.id !== model.id || catalog.models.get(row.id) !== row)
                return notify("AIR selection or catalog changed while the picker was open. Open /context again.", "warning");
            const preference = name === "default" ? null : name;
            const next = tier(row, preference).window || observedWindow(row, engines);
            if (!next || (context.isIdle() && !contextFits(context, next, ctx.model.contextWindow, () => projectedTokens(context))))
                return notify("The transcript and output reserve do not fit this context. Use /compact on the current profile, then retry /context.", "warning");
            choices.set(row.id, preference);
            pi.appendEntry(entryType, {model: row.id, profile: preference});
            await publish();
            notify(row.id + " · " + name + " · " + next + " tokens");
        }});
    pi.registerCommand("air-status", {description: "Show this session’s latest AIR ticket",
        handler: async args => {
            const requests = currentRequests();
            const key = args.trim();
            const req = key ? [...requests, latest].find(r => matchesRequest(r, key)) : requests.at(-1) ?? latest;
            if (!req) return notify("No matching AIR request on this branch.");
            if (!req.ticket) return notify(requestKey(req) + " · " + req.text);
            try {
                await inspect(req, req.branch.signal);
                if (req.branch === branch) notify(req.ticket + " · " + req.text);
            } catch (error) {
                if (req.branch === branch) notify(error.message, "warning");
            }
        }});
    pi.registerCommand("air-cancel", {description: "Cancel this branch’s active AIR request by ticket",
        handler: async args => {
            const requests = currentRequests();
            const key = args.trim();
            if (!key && requests.length > 1)
                return notify("Choose an active request with /air-cancel ID: " + requests.map(requestKey).join(", "));
            const req = key ? requests.find(r => matchesRequest(r, key)) : requests[0];
            if (!req) return notify("No matching active AIR request to cancel.");
            req.abort.abort();
            notify(snapshotText(requestKey(req), {State: "cancellation requested"}));
        }});
    await refresh();
}
