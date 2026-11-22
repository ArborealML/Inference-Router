const positive = n => Number.isSafeInteger(n) && n > 0;

export function parseCatalog(snapshot) {
    const models = new Map();
    for (const engine of snapshot.Engines) {
        for (const row of engine.Models) {
            if (typeof row.Id !== "string" || !row.Id) throw new Error("Invalid AIR catalog model");
            const model = models.get(row.Id) ?? {id: row.Id, profiles: new Map(), defaults: [], aliases: new Set()};
            models.set(row.Id, model);
            for (const alias of row.Aliases) model.aliases.add(alias);
            for (const profile of row.Profiles) {
                if (!positive(profile.MaxContext) || !profile.Name) throw new Error("Invalid AIR profile");
                const name = profile.Name;
                const budgets = model.profiles.get(name) ?? [];
                budgets.push({engine: engine.Name, window: profile.MaxContext, slots: profile.MaxSlots});
                model.profiles.set(name, budgets);
            }
            const def = row.Profiles.find(p => p.Name === row.DefaultProfile);
            model.defaults.push(def ? {name: def.Name, window: def.MaxContext} : null);
        }
    }
    return {models, defaultId: snapshot.DefaultModel};
}

export function tier(model, name) {
    if (name) {
        const budgets = model.profiles.get(name);
        if (!budgets) return undefined;
        return {name, window: Math.min(...budgets.map(b => b.window))};
    }
    const defaults = model.defaults;
    const agreed = defaults.every(d => d && d.name === defaults[0]?.name);
    if (agreed) return tier(model, defaults[0].name);
    // With differing defaults no exact profile can represent the router's choice.
    const minimum = defaults.every(Boolean) ? Math.min(...defaults.map(d => d.window)) : 0;
    return {name: null, window: minimum};
}

export const outputBudget = window => Math.min(8192, Math.max(1, Math.floor(window / 4)));

export function requestFits(window, estimatedInput, reserve = outputBudget(window)) {
    return Number.isFinite(estimatedInput) && estimatedInput + reserve <= window;
}

export function contextFits(ctx, target, current, estimate) {
    if (target >= current) return true;
    const usage = ctx.getContextUsage()?.tokens ?? estimate?.();
    return requestFits(target, usage);
}

export function observedWindow(model, engines) {
    const windows = engines.filter(e => e.Healthy && e.LoadedModels.includes(model.id)).map(e => e.ActiveContextWindow);
    return windows.length && windows.every(positive) ? Math.min(...windows) : 0;
}

export function piModel(model, selected, engines) {
    const requested = tier(model, selected);
    const window = requested?.window || observedWindow(model, engines);
    const loaded = engines.filter(e => e.Healthy && e.LoadedModels.includes(model.id)).map(e => e.Engine);
    return {
        id: model.id, name: model.id + (loaded.length ? ' (loaded on ' + loaded.join(', ') + ')' : ' (configured)'),
        contextWindow: window || 4096, maxTokens: outputBudget(window || 4096),
        reasoning: true, input: ["text"], cost: {input: 0, output: 0, cacheRead: 0, cacheWrite: 0},
        compat: {supportsStore: false, supportsDeveloperRole: false, supportsStrictMode: false,
            supportsReasoningEffort: true, maxTokensField: "max_tokens", thinkingFormat: "chat-template",
            chatTemplateKwargs: {enable_thinking: {$var: "thinking.enabled"}, preserve_thinking: true,
                reasoning_effort: {$var: "thinking.effort", omitWhenOff: true}}}
    };
}
