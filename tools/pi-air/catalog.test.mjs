import assert from "node:assert/strict";
import test from "node:test";
import {contextFits, observedWindow, parseCatalog, piModel, requestFits, tier} from "./catalog.mjs";

const engine = (name, window, defaultProfile = "short") => ({Name: name, Models: [{
    Id: "example", Aliases: ["ex"], DefaultProfile: defaultProfile, Profiles: [
        {Name: "short", MaxContext: window, MaxSlots: 2}, {Name: "large", MaxContext: 262144, MaxSlots: 1}
    ]
}]});
const model = (...engines) => parseCatalog({Engines: engines}).models.get("example");

test("one Pi model shares only the safe minimum for each named tier", () => {
    const row = model(engine("a", 32768), engine("b", 65536));
    assert.deepEqual(tier(row, null), {name: "short", window: 32768});
    assert.deepEqual(tier(row, "large"), {name: "large", window: 262144});
    assert.equal(row.profiles.get("short").length, 2);
    assert.equal(tier(row, "removed"), undefined);
});

test("different engine defaults can request a common minimum without pinning a different profile", () => {
    const row = model(engine("a", 32768), engine("b", 65536, "large"));
    assert.deepEqual(tier(row, null), {name: null, window: 32768});
});

test("cold configured budgets and healthy observed windows remain distinct", () => {
    const row = model(engine("a", 32768));
    const live = [{Engine: "a", Healthy: true, LoadedModels: ["example"], ActiveContextWindow: 131072}];
    assert.equal(observedWindow(row, []), 0);
    assert.equal(observedWindow(row, live), 131072);
    assert.equal(piModel(row, "large", live).contextWindow, 262144);
    assert.match(piModel(row, null, []).name, /configured/);
    assert.match(piModel(row, null, live).name, /loaded on a/);
});

test("downsizing includes generation reserve and can estimate the post-compaction projection", () => {
    const ctx = {getContextUsage: () => ({tokens: 24577})};
    assert.equal(contextFits(ctx, 32768, 262144), false);
    ctx.getContextUsage = () => ({tokens: 24576});
    assert.equal(contextFits(ctx, 32768, 262144), true);
    ctx.getContextUsage = () => ({tokens: null});
    assert.equal(contextFits(ctx, 32768, 262144), false);
    assert.equal(contextFits(ctx, 32768, 262144, () => 10000), true);
});

test("a later prompt must fit even when the selected profile has stayed the same", () => {
    assert.equal(requestFits(32768, 24576), true);
    assert.equal(requestFits(32768, 24577), false);
    assert.equal(requestFits(32768, 32770, 1), false);
});

test("default output limits follow the model family", () => {
    for (const [id, limit] of [["qwen", 32768], ["deepseek-v4.1-flash-1017-iq2-xxs", 65536],
                               ["deepseek-v4.1-flash-1017-maya-xs-v2", 65536]]) {
        const row = model(engine("a", 262144));
        row.id = id;
        assert.equal(piModel(row, null, []).maxTokens, limit);
    }
});
