using System.Collections.ObjectModel;

namespace Arboreal.InferenceRouter;

public sealed partial class RouterCore
{
    internal sealed class ConfigurationGeneration(long number, RouterConfig settings, ReloadConfiguration? file,
        Dictionary<string, EngineRuntime> engines)
    {
        internal Dictionary<string, string> Aliases { get; } = BuildAliases(settings);
        private static Dictionary<string, string> BuildAliases(RouterConfig config)
        {
            var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var engine in config.Engines.Values)
                foreach (var (name, model) in engine.Catalog)
                {
                    aliases.TryAdd(name, name);
                    foreach (var alias in model.Aliases) aliases.TryAdd(alias, name);
                }
            return aliases;
        }
        internal long Number { get; } = number;
        internal RouterConfig Settings { get; } = settings;
        internal ReloadConfiguration? File { get; } = file;
        internal IReadOnlyDictionary<string, EngineRuntime> Engines { get; } =
            new ReadOnlyDictionary<string, EngineRuntime>(engines);
        internal ICapabilitySource[] Capabilities { get; } = [.. engines.Values];
    }

    internal enum NativeTargetResetStatus { Reset, Stale, Closing, Unavailable }

    private readonly string? _nativeResetStartupInstance;
    private ConfigurationGeneration _generation;
    private bool _closing;
    private int _engineAdminUses;
    private TaskCompletionSource? _reloadHousekeeping;
    internal Action? BeforeReloadCommitForTests;
    internal Action<EngineRuntime>? AfterReloadStageForTests;
    internal Action? BeforeReloadActivationHandoffForTests;
    internal Action? BeforeReloadRegistrationClaimForTests;
    internal Task? ReloadHousekeepingForTests
    {
        get { lock (_gate) return _reloadHousekeeping?.Task; }
    }

    internal ConfigurationGeneration CurrentGeneration => Volatile.Read(ref _generation);
    internal CancellationToken ShutdownToken => _shutdownToken;
    internal bool Closing => Volatile.Read(ref _closing);

    internal EngineRuntime? AcceptEngineAdmin(string name)
    {
        lock (_gate)
        {
            var engine = _closing || _localGroups.ContainsKey(name) ? null : CurrentGeneration.Engines.GetValueOrDefault(name);
            if (engine is not null) _engineAdminUses++;
            return engine;
        }
    }

    internal void ReleaseEngineAdmin()
    {
        lock (_gate) _engineAdminUses--;
    }

    internal NativeTargetResetStatus ResetNativeTarget(string instance, long expectedGeneration, string engineName,
        string model, string effectiveProfile, CancellationToken caller)
    {
        lock (_gate)
        {
            if (_closing) return NativeTargetResetStatus.Closing;
            var current = CurrentGeneration;
            if (_nativeResetStartupInstance is null || instance != _nativeResetStartupInstance ||
                current.Number != expectedGeneration)
                return NativeTargetResetStatus.Stale;
            if (current.Engines.GetValueOrDefault(engineName) is not { } engine || engine.Name != engineName)
                return NativeTargetResetStatus.Unavailable;

            lock (engine.ReloadTransitionGate)
            {
                caller.ThrowIfCancellationRequested();
                if (!engine.ResetNativeTargetUnderGate(model, effectiveProfile))
                    return NativeTargetResetStatus.Unavailable;
            }
        }
        SignalDispatch();
        return NativeTargetResetStatus.Reset;
    }

    internal async Task<(ConfigReloadStatus Status, long Generation)> ApplyConfigurationAsync(
        ReloadConfiguration candidate, long expected, CancellationToken caller)
    {
        ConfigurationGeneration previous;
        lock (_gate)
        {
            previous = CurrentGeneration;
            if (_closing) return (ConfigReloadStatus.Closing, previous.Number);
            if (previous.Number != expected) return (ConfigReloadStatus.Stale, previous.Number);
        }
        var file = previous.File;
        if (file is null || _admission is not null) return (ConfigReloadStatus.RestartRequired, previous.Number);

        var change = file.Compare(candidate, out var addedNames, out var removedNames, out var aliasesChanged);
        if (change is ConfigReloadStatus.RestartRequired) return (change, previous.Number);
        if (change is ConfigReloadStatus.Unchanged)
        {
            lock (_gate)
            {
                if (_closing) return (ConfigReloadStatus.Closing, CurrentGeneration.Number);
                if (!ReferenceEquals(previous, CurrentGeneration)) return (ConfigReloadStatus.Stale, CurrentGeneration.Number);
                return (caller.IsCancellationRequested ? ConfigReloadStatus.Cancelled : change, previous.Number);
            }
        }

        var settings = candidate.Config;
        var removed = removedNames.Select(name => previous.Engines[name]).ToArray();
        var routes = previous.Engines.Where(pair => !removedNames.Contains(pair.Key, StringComparer.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var retained = routes.Values.ToArray();
        var added = new List<EngineRuntime>(addedNames.Length);
        var published = false;
        try
        {
            if (addedNames.Length != 0)
            {
                foreach (var name in addedNames)
                {
                    caller.ThrowIfCancellationRequested();
                    var runtime = CreateOrdinaryEngine(settings.Engines[name], settings, candidate);
                    added.Add(runtime);
                    routes.Add(name, runtime);
                    AfterReloadStageForTests?.Invoke(runtime);
                }
            }
            var next = new ConfigurationGeneration(checked(expected + 1), settings, candidate, routes);
            var target = ResolveCanonical(settings.DefaultModel, next);
            EngineRuntime? witness = null;
            if (!routes.Values.Any(engine => engine.DeclaresModel(target)))
            {
                witness = retained.FirstOrDefault(engine => engine.ReportsReloadDefault(target));
                if (witness is null) return (ConfigReloadStatus.Invalid, previous.Number);
            }

            var topology = aliasesChanged || added.Count != 0 || removed.Length != 0;
            var housekeeping = topology ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) : null;
            var guarded = removed.Append(witness).OfType<EngineRuntime>().OrderBy(engine => engine.Name, StringComparer.Ordinal).ToArray();
            BeforeReloadCommitForTests?.Invoke();
            lock (_gate)
            {
                if (_closing) return (ConfigReloadStatus.Closing, CurrentGeneration.Number);
                if (!ReferenceEquals(previous, CurrentGeneration)) return (ConfigReloadStatus.Stale, CurrentGeneration.Number);
                if (_reloadHousekeeping is not null || (topology && !IsReloadIdle(previous)))
                    return (ConfigReloadStatus.Busy, previous.Number);

                var entered = 0;
                try
                {
                    foreach (var engine in guarded)
                    {
                        Monitor.Enter(engine.ReloadTransitionGate);
                        entered++;
                    }
                    if (removed.Any(engine => !engine.CanRetireExternal))
                        return (ConfigReloadStatus.Busy, previous.Number);
                    if (witness is not null && !witness.ReportsReloadDefault(target))
                        return (ConfigReloadStatus.Invalid, previous.Number);
                    if (caller.IsCancellationRequested) return (ConfigReloadStatus.Cancelled, previous.Number);

                    foreach (var engine in added)
                    {
                        if (settings.Engines[engine.Name].LifecycleGroup is { } groupName)
                        {
                            var group = _localGroups.Values.First(group => group.Name == groupName);
                            group.Attach(engine);
                            _localGroups[engine.Name] = group;
                        }
                        engine.TryReserveExternalPublication();
                    }
                    foreach (var engine in removed) engine.FenceExternalRetirement();
                    _reloadHousekeeping = housekeeping;
                    Volatile.Write(ref _generation, next);
                    published = true;
                }
                finally
                {
                    while (entered != 0) Monitor.Exit(guarded[--entered].ReloadTransitionGate);
                }
            }

            _events.Append(new RouterEvent { Kind = "catalog.changed" });
            if (housekeeping is not null)
                await FinishTopologyReloadAsync(removed, added, housekeeping).ConfigureAwait(false);
            SignalDispatch();
            return (ConfigReloadStatus.Applied, next.Number);
        }
        finally
        {
            if (!published)
                foreach (var engine in added) await engine.DisposeAsync().ConfigureAwait(false);
        }
    }

    private bool IsReloadIdle(ConfigurationGeneration generation)
    {
        if (_localGroups.Values.Any(group => !group.IsIdle) || _engineAdminUses != 0 || _queues.Values.Any(queue => queue.Depth != 0)) return false;
        // A first-send lease is only taken by an admitted request. Under the router gate no new
        // logical use can start; detached cleanup can only lower the remaining physical counts.
        return !generation.Engines.Values.Any(engine => engine.Inflight != 0 || engine.Switching || engine.InferenceUses != 0);
    }

    private async Task FinishTopologyReloadAsync(EngineRuntime[] removed, List<EngineRuntime> added,
        TaskCompletionSource completion)
    {
        var handedOff = false;
        try
        {
            foreach (var engine in removed)
            {
                try { await engine.DisposeAsync().ConfigureAwait(false); }
                catch (Exception) { Interlocked.Increment(ref _reloadCleanupFaults); }
            }
            if (added.Count != 0)
            {
                BeforeReloadActivationHandoffForTests?.Invoke();
                _ = Task.Run(() => ActivateReloadEnginesAsync(added, completion.Task));
                handedOff = true;
            }
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _reloadCleanupFaults);
        }
        finally
        {
            if (!handedOff)
                foreach (var engine in added) engine.EndExternalPublication();
            lock (_gate)
            {
                _reloadHousekeeping = null;
                completion.TrySetResult();
            }
        }
    }

    private long _reloadCleanupFaults;
    internal long ReloadCleanupFaults => Interlocked.Read(ref _reloadCleanupFaults);

    private async Task ActivateReloadEnginesAsync(List<EngineRuntime> engines, Task housekeeping)
    {
        await housekeeping.ConfigureAwait(false);
        foreach (var engine in engines)
        {
            try
            {
                BeforeReloadRegistrationClaimForTests?.Invoke();
                if (!TryClaimReloadActivation(engine, EngineRuntime.ExternalActivation.Registration)) continue;
                _events.Append(new RouterEvent { Kind = "engine.registered", Engine = engine.Name });
                if (TryClaimReloadActivation(engine, EngineRuntime.ExternalActivation.FirstProbe))
                    engine.StartHealthLoop(_shutdownToken);
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _reloadCleanupFaults);
            }
            finally
            {
                engine.EndExternalPublication();
            }
        }
    }

    private bool TryClaimReloadActivation(EngineRuntime engine, EngineRuntime.ExternalActivation phase)
    {
        lock (_gate)
        lock (engine.ReloadTransitionGate)
            return !_closing && engine.TryClaimReloadActivation(phase);
    }
}
