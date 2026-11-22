import {createAgentSession, DefaultResourceLoader, ModelRuntime, SessionManager, SettingsManager}
    from "@earendil-works/pi-coding-agent";

function heldTool(onHold) {
    let release;
    return {
        tool: {
            name: "fixture_hold",
            label: "Hold",
            description: "Wait for the fixture to release the result",
            parameters: {type: "object", properties: {}},
            async execute(_id, _args, signal) {
                signal?.throwIfAborted();
                await new Promise((resolve, reject) => {
                    const abort = () => reject(signal.reason);
                    signal?.addEventListener("abort", abort, {once: true});
                    release = () => {
                        signal?.removeEventListener("abort", abort);
                        resolve();
                    };
                    onHold();
                });
                return {content: [{type: "text", text: "Released the tool"}], details: undefined};
            }
        },
        release: () => release()
    };
}

async function runChild(ctx, inherit) {
    const settingsManager = SettingsManager.inMemory({retry: {enabled: false}, compaction: {enabled: false}});
    const agentDir = process.env.PI_CODING_AGENT_DIR;
    const modelRuntime = await ModelRuntime.create();
    const model = ctx.modelRegistry.find("air", "child.fixture");
    const ready = Promise.withResolvers();
    const hold = heldTool(ready.resolve);
    if (inherit) modelRuntime.registerProvider("air", ctx.modelRegistry.getRegisteredProviderConfig("air"));
    const resourceLoader = new DefaultResourceLoader({
        cwd: ctx.cwd, agentDir, settingsManager, noExtensions: true, noSkills: true, noContextFiles: true,
        additionalExtensionPaths: inherit ? [] : [process.env.PI_AIR_FIXTURE_EXTENSION],
    });
    await resourceLoader.reload();
    const {session} = await createAgentSession({
        cwd: ctx.cwd, agentDir, modelRuntime, settingsManager, resourceLoader, model,
        sessionManager: SessionManager.inMemory(ctx.cwd),
        tools: inherit ? [] : ["fixture_hold"],
        customTools: inherit ? [] : [hold.tool],
        sessionStartEvent: {type: "session_start", reason: "startup"},
    });
    try {
        const errors = [];
        await session.bindExtensions({mode: "print", onError: error => errors.push(error)});
        if (inherit) {
            await session.prompt("inherited child");
            if (session.messages.at(-1)?.stopReason !== "stop")
                throw new Error("Inherited child did not complete");
        } else {
            await session.prompt("/context narrow");
            const work = session.prompt("independent child");
            try {
                await Promise.race([ready.promise, work.then(() => { throw new Error("Child tool did not hold"); })]);
                await session.setModel(modelRuntime.getModel("air", "qwen.fixture"));
                hold.release();
                await work;
            } catch (error) {
                await session.abort();
                throw error;
            }
        }
        if (errors.length) throw new Error(JSON.stringify(errors));
        ctx.ui.notify("Child thread " + (inherit ? "inherited " : "independent ") + session.sessionId);
    } finally {
        await session.extensionRunner.emit({type: "session_shutdown", reason: "quit"});
        session.dispose();
    }
}

async function runRegistryCall(args, ctx) {
    if (!ctx.signal) throw new Error("The parent tool is not held");
    const sessionId = args.trim() === "same-session" ? ctx.sessionManager.getSessionId() : undefined;
    const model = ctx.modelRegistry.find("air", "child.fixture");
    const result = await ctx.modelRegistry.streamSimple(model, {
        messages: [{role: "user", content: "direct registry call", timestamp: Date.now()}],
    }, {sessionId, signal: ctx.signal, onPayload: payload => payload}).result();
    ctx.ui.notify("Registry call " + result.stopReason + (result.errorMessage ? ": " + result.errorMessage : ""));
}

export default function(pi) {
    let holdContext;
    const hold = heldTool(() => holdContext.ui.notify("Tool held"));
    pi.on("session_start", (_event, ctx) => { holdContext = ctx; });
    pi.registerTool(hold.tool);
    pi.registerCommand("fixture-release", {handler: () => hold.release()});
    pi.registerCommand("fixture-child", {handler: (args, ctx) => runChild(ctx, args.trim() === "inherited")});
    pi.registerCommand("fixture-registry", {handler: runRegistryCall});
    pi.registerCommand("fixture-custom", {handler: () => pi.sendMessage({
        customType: "fixture", content: "Run a custom turn", display: false
    }, {triggerTurn: true})});
}
