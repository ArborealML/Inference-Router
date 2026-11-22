import { estimateTokens } from "@earendil-works/pi-coding-agent";
import { openAICompletionsApi } from "@earendil-works/pi-ai/compat";
import { randomUUID } from "node:crypto";
import { setTimeout as delay } from "node:timers/promises";

import { contextFits, observedWindow, parseCatalog, piModel, requestFits, tier } from "./catalog.mjs";

const entryType = "air-context";
const clean = value => String(value ?? "").replace(/[\x00-\x1f\x7f-\x9f]/g, " ").slice(0, 450);

export default async function air(pi) {
    const base = process.env.PI_AIR_BASE_URL;
    if (!base) return;
    const url = new URL(base);
    if (url.origin + "/v1" !== base || url.protocol !== "http:" || url.hostname !== "127.0.0.1") {
        throw new Error("Invalid local AIR listener");
    }
    const auth = process.env.AIR_API_KEY;
    const adapter = openAICompletionsApi();
    let catalog, etag, ctx, lifetime, branch, latest;
    let engines = [];
    let choices = new Map();
    let budgetRefused = false;
    const active = new Set();
    const currentRequests = () => Array.from(active).filter(req => req.branch === branch);
    const requestKey = req => req.ticket || req.clientId;
    const matchesRequest = (req, id) => req && (req.ticket === id || req.clientId === id);
    function updateFooter() {
        ctx?.ui.setStatus("air-ticket", currentRequests().at(-1)?.text);
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
        if (!response.ok) throw new Error("Ticket HTTP " + response.status);
        const state = await response.json();
        if (state.Id !== req.ticket || state.ThreadId !== req.thread) throw new Error("Ticket identity mismatch");
        if (req.branch === branch) {
            req.text = snapshotText(req.ticket, state);
            if (currentRequests().at(-1) === req) updateFooter();
            if (state.ErrorCode && !req.reported) {
                req.reported = true;
                notify(req.text, "error");
            }
        }
        return ["Completed", "Failed", "Cancelled"].includes(state.State);
    }

    async function watchTicket(req) {
        try {
            while (!req.watch.signal.aborted) {
                if (await inspect(req, req.watch.signal)) break;
                await delay(700, undefined, {signal: req.watch.signal});
            }
        } catch { /* A lost admin connection never interrupts the inference stream. */ }
    }

    function streamSimple(model, transcript, options = {}) {
        const row = catalog.models.get(model.id);
        const target = row && tier(row, selected(row.id));
        if (!target) throw new Error("AIR selection was removed. Choose an available /model or /context default.");
        const capacity = target.window || observedWindow(row, engines);
        if (!capacity) throw new Error("AIR has no configured or observed context budget for " + model.id);
        const current = definition(row);
        // The new prompt and tool definitions can arrive after /context validated the prior turn.
        // Compaction and other auxiliary calls may have their own, shorter transcript.
        const estimatedInput = transcript.messages.reduce((total, message) => total + estimateTokens(message), 0);
        const sessionTokens = options.sessionId && options.sessionId === ctx?.sessionManager.getSessionId()
            ? ctx.getContextUsage()?.tokens : undefined;
        const reserve = Math.min(current.maxTokens, options.maxTokens ?? current.maxTokens);
        if (!requestFits(capacity, Math.max(estimatedInput, sessionTokens ?? 0), reserve)) {
            budgetRefused = true;
            throw new Error("The estimated AIR prompt and output reserve exceed this window. Select a larger /context or use /compact.");
        }
        budgetRefused = false;
        const thread = ctx?.sessionManager.getSessionId() || options.sessionId;
        if (!thread) throw new Error("AIR needs a Pi session identity");
        const req = {thread, model: model.id, branch, clientId: "request-" + randomUUID(), watch: new AbortController(), abort: new AbortController()};
        active.add(req);
        latest = req;
        req.text = snapshotText(requestKey(req), {State: "submitting", Model: model.id});
        updateFooter();
        const signal = options.signal ? AbortSignal.any([options.signal, req.abort.signal]) : req.abort.signal;
        const underlying = options.fetch ?? fetch;
        let sent = false;
        const original = adapter.streamSimple({...model, contextWindow: capacity, maxTokens: current.maxTokens}, transcript, {
            ...options, signal, maxTokens: reserve, maxRetries: 0,
            fetch: async (input, init) => {
                if (sent) throw new Error("AIR already received this Pi request");
                if (String(input) !== base + "/chat/completions" || init?.method?.toUpperCase() !== "POST")
                    throw new Error("Unexpected AIR inference destination");
                const liveRow = catalog.models.get(model.id);
                const liveTier = liveRow && tier(liveRow, selected(model.id));
                if (req.branch !== branch || !liveTier || liveTier.name !== target.name ||
                    (liveTier.window || observedWindow(liveRow, engines)) !== capacity)
                    throw new Error("AIR changed the selected model or profile before dispatch. Check /context.");
                const headers = new Headers(init.headers);
                headers.set("X-Inference-Thread", thread);
                if (target.name) headers.set("X-Inference-Profile", target.name);
                else headers.delete("X-Inference-Profile");
                headers.set("X-Inference-Required-Context", String(capacity));
                if (!auth) headers.delete("authorization");
                sent = true;
                const response = await underlying(input, {...init, headers, redirect: "error"});
                req.ticket = response.headers.get("X-Inference-Ticket");
                if (req.ticket && req.branch === branch) {
                    req.text = snapshotText(req.ticket, {State: "admitted"});
                    updateFooter();
                    void watchTicket(req);
                }
                return response;
            }
        });
        void original.result().then(async message => {
            req.watch.abort();
            active.delete(req);
            if (req.branch !== branch) return;
            updateFooter();
            if (req.ticket) await inspect(req, req.branch.signal).catch(() => {});
            if (message.stopReason === "error" && req.branch === branch && !req.reported)
                notify(snapshotText(req.ticket, {State: "Failed", Error: message.errorMessage}), "error");
        }).catch(() => {
            req.watch.abort();
            active.delete(req);
            if (req.branch === branch) updateFooter();
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
        latest = undefined;
        ctx = context;
        updateFooter();
        budgetRefused = false;
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
        ctx = lifetime = branch = latest = undefined;
    });
    pi.on("agent_settled", async () => { if (ctx) await publish(); });
    pi.on("cache_warming_decision", (_event, context) => {
        if (context.model?.provider === "air") return {action: "stop"};
    });
    pi.on("session_before_compact", (event, context) => {
        if (context.model?.provider === "air" && (event.willRetry || (budgetRefused && event.reason !== "manual"))) {
            notify("AIR request ended. Use /compact or select a larger profile before submitting again.", "warning");
            return {cancel: true};
        }
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
            if (!context.isIdle()) return notify("Wait for the current request before changing context.", "warning");
            const preference = name === "default" ? null : name;
            const next = tier(row, preference).window || observedWindow(row, engines);
            if (!next || !contextFits(context, next, ctx.model.contextWindow, () => projectedTokens(context)))
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
            if (!req.ticket) return notify(req.text);
            try {
                await inspect(req, req.branch.signal);
                if (req.branch === branch) notify(req.text);
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
