using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Arboreal.InferenceRouter.Cli;

internal static class AirTuneSingleModel
{
    private static readonly TimeSpan WitnessTime = TimeSpan.FromSeconds(5);
    private static readonly TimeProvider Clock = TimeProvider.System;

    internal static async Task<TuneRunEvidence> MeasureAsync(ITuneAdmission? admission, EngineDefinition engine,
        WorkloadManifest manifest, string recipe, CancellationToken cancellationToken)
    {
        if (manifest.Tiers is not [{ PlannedCap: 1 } tier])
            throw new ArgumentException("The singleton runner requires one tier with a concurrency cap of one.", nameof(manifest));
        var runId = Guid.NewGuid().ToString("N");
        var policy = manifest.Policy;
        var workload = new List<TuneRequestEvidence>();
        var samples = new List<TuneSampleEvidence>();
        TuneWitness? resident = null;

        bool? released = null;

        if (admission is null)
            return Finish("refused", "admission_unavailable");

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(policy.TierSeconds));
        using var work = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
        work.CancelAfter(TimeSpan.FromSeconds(policy.TierSeconds - policy.AbortDrainReserveSeconds));
        try
        {
            using var intake = CancellationTokenSource.CreateLinkedTokenSource(work.Token);
            intake.CancelAfter(WitnessTime);
            var lease = await admission.AcquireAsync(engine, manifest.Model, tier.Profile, recipe, intake.Token).ConfigureAwait(false);
            if (lease is null)
                return Finish("refused", "admission_refused");

            (string Status, string? Reason) outcome;
            try
            {
                outcome = intake.IsCancellationRequested
                    ? ("refused", "admission_timed_out")
                    : await MeasureLeasedAsync(lease).ConfigureAwait(false);
            }
            finally
            {
                released = await ReleaseAsync(lease, deadline.Token).ConfigureAwait(false);
            }
            return released == true ? Finish(outcome.Status, outcome.Reason) : Finish("inconclusive", "admission_release_unproved");
        }
        catch (Exception error) when ((released == false || error is OperationCanceledException or HttpRequestException or
            IOException or InvalidDataException or JsonException or OverflowException) && IsRecoverableAdapterFailure(error))
        {
            return released == false ? Finish("inconclusive", "admission_release_unproved") :
                Finish(resident is null ? "refused" : "inconclusive", work.IsCancellationRequested
                    ? "budget_exhausted" : "measurement_evidence_unavailable");
        }

        async Task<(string Status, string? Reason)> MeasureLeasedAsync(ITuneResident lease)
        {
            var initial = await ObserveAsync(lease, null, work.Token).ConfigureAwait(false);
            if (initial is null || !ValidResident(initial, manifest, tier, recipe))
                return ("refused", "preflight_refusal");

            var target = JsonSerializer.SerializeToUtf8Bytes(initial.Target, AirTuneRunJsonContext.Default.TuneTarget);
            var hardware = JsonSerializer.SerializeToUtf8Bytes(initial.Hardware, AirTuneRunJsonContext.Default.TuneHardware);
            if (!FitsRecord(target) || !FitsRecord(hardware))
                return ("refused", "invalid_resident_evidence");

            resident = initial;
            var bodies = new List<FittedTunePrompt>(tier.Requests.Length);
            foreach (var coordinate in tier.Requests)
            {
                var generator = new AirTuneTextGenerator(manifest.Seed, tier.Profile, coordinate.Concurrency,
                    coordinate.Wave, coordinate.Worker);
                var prompt = await AirTunePromptFit.FitAsync(generator, manifest.Model, policy.TargetOutputTokens,
                    checked((int)tier.NominalPromptMin), checked((int)tier.NominalPromptMax),
                    (body, token) => CountAsync(lease, body, token), work.Token)
                    .ConfigureAwait(false);
                if (prompt is null || prompt.Tokens + (long)policy.TargetOutputTokens > tier.ConfiguredContext - initial.ContextReserve)
                    return ("refused", "invalid_workload");
                bodies.Add(prompt);
                workload.Add(new(coordinate, prompt.Tokens, policy.TargetOutputTokens, prompt.Records, prompt.TailBytes,
                    Digest(generator.Candidate(prompt.Records, prompt.TailBytes)), Digest(prompt.Body)));
            }

            for (var index = 0; index < bodies.Count; index++)
            {
                var before = await ObserveAsync(lease, null, work.Token).ConfigureAwait(false);
                if (!SameIdleResident(before, initial, target))
                    return ("inconclusive", "resident_changed");
                if (!SameStableHardware(before?.Hardware, hardware))
                    return ("inconclusive", "resident_hardware_changed");

                var request = Guid.NewGuid();
                var exchange = await ReadSampleAsync(lease, request, manifest.Model, bodies[index], policy, work.Token)
                    .ConfigureAwait(false);
                var after = await ObserveAsync(lease, request, deadline.Token).ConfigureAwait(false);
                var native = after?.Sample;
                var settled = SameIdleResident(after, initial, target) && native is { Settled: true } && native.Request == request;
                bool? stableHardware = after is null ? null : SameStableHardware(after.Hardware, hardware);
                string? reason;
                string status;
                if (native is null || !settled)
                {
                    status = "inconclusive";
                    reason = "settlement_unproved";
                }
                else if (stableHardware != true)
                {
                    status = "inconclusive";
                    reason = "resident_hardware_changed";
                }
                else if (native.Attempts > 1 || native.Errors != 0 || native.ReusedTokens is < 0 or > 64 ||
                    native.PromptTokens > 0 && native.PromptTokens != bodies[index].Tokens ||
                    native.ServedOutputTokens is { } served && exchange.Observation.CompletionTokens is { } streamed && served != streamed ||
                    native.OutputAllowance > 0 && native.OutputAllowance != policy.TargetOutputTokens)
                {
                    status = "failed";
                    reason = "native_workload_mismatch";
                }
                else if (exchange.Reason is { } streamFailure)
                {
                    status = "failed";
                    reason = streamFailure;
                }
                else if (!native.Admitted || native.Attempts != 1 || native.PromptTokens <= 0 || native.OutputAllowance <= 0 ||
                    native.ReusedTokens is null || native.ServedOutputTokens is not > 0 || native.DecodeWorkTokens is not > 0 ||
                    native.PrefillMicroseconds is not > 0 || native.DecodeMicroseconds is not > 0)
                {
                    status = "inconclusive";
                    reason = "native_evidence_unavailable";
                }
                else
                {
                    status = "passed";
                    reason = null;
                }
                var stream = exchange.Observation;
                var settlement = new TuneSettlementEvidence(settled, after?.Generation, after?.Exclusive, after?.Active,
                    after?.Queued, stableHardware);
                samples.Add(new(request, tier.Requests[index], status, reason, stream.OutcomeName, stream.EndReasonName,
                    UsageName(stream.UsageAvailability), stream.FirstSemanticOutput?.Ticks, exchange.Terminal?.Ticks,
                    stream.PromptTokens, stream.CompletionTokens, native, settlement));
                if (reason is not null)
                    return (status, reason);
            }
            return ("passed", null);
        }

        TuneRunEvidence Finish(string status, string? reason)
        {
            return new(runId, manifest.Engine, manifest.Model, tier.Profile, manifest.Seed, status, reason, resident, released,
                [.. workload], [.. samples], resident is null ? null : BuildSummary(runId, resident, manifest, workload, samples, status));
        }
    }

    private static string UsageName(StreamUsageAvailability usage) => usage switch
    {
        StreamUsageAvailability.Available => "available",
        StreamUsageAvailability.Invalid => "invalid",
        _ => "unavailable",
    };

    private static async Task<bool> ReleaseAsync(ITuneResident lease, CancellationToken deadline)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(deadline);
        timeout.CancelAfter(WitnessTime);
        var token = timeout.Token;
        try
        {
            // A lease can lose its peer or fail to complete asynchronously. The runner still saves
            // an inconclusive record, and only the authority can resolve the fence afterward.
            var released = await Task.Run(() => lease.ReleaseAsync(token), CancellationToken.None)
                .WaitAsync(token).ConfigureAwait(false);
            return released && !token.IsCancellationRequested;
        }
        catch (Exception error) when (IsRecoverableAdapterFailure(error))
        {
            return false;
        }
    }

    private static bool IsRecoverableAdapterFailure(Exception error) => error switch
    {
        OutOfMemoryException or StackOverflowException or AccessViolationException or SEHException => false,
        AggregateException aggregate => aggregate.InnerExceptions.All(IsRecoverableAdapterFailure),
        _ => error.InnerException is not { } inner || IsRecoverableAdapterFailure(inner),
    };

    private static async Task<int> CountAsync(ITuneResident lease, byte[] body, CancellationToken cancellationToken)
    {
        using var time = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        time.CancelAfter(WitnessTime);
        var count = await lease.CountAsync(body, time.Token).ConfigureAwait(false);
        time.Token.ThrowIfCancellationRequested();
        return count;
    }

    private static async Task<TuneWitness?> ObserveAsync(ITuneResident lease, Guid? sample, CancellationToken cancellationToken)
    {
        using var time = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        time.CancelAfter(WitnessTime);
        var challenge = Guid.NewGuid();
        try
        {
            var witness = await lease.ObserveAsync(challenge, sample, time.Token).ConfigureAwait(false);
            return !time.IsCancellationRequested && witness?.Challenge == challenge ? witness : null;
        }
        catch (Exception error) when (error is OperationCanceledException or HttpRequestException or IOException or JsonException)
        {
            return null;
        }
    }

    private static bool ValidResident(TuneWitness witness, WorkloadManifest manifest, WorkloadTier tier, string recipe)
    {
        var target = witness.Target;
        var hardware = witness.Hardware;
        return witness.Generation > 0 && AirTuneRecord.IsRedactedId(witness.InstanceId) &&
            witness is { Exclusive: true, ExactSettings: true, Active: 0, Queued: 0, ContextReserve: >= 0, Sample: null } &&
            target.IsValid() && hardware.IsValid() && target.Model.Value == manifest.Model && target.Tier.Value == tier.Profile &&
            target.RecipeSha256.Value == recipe && target.NominalContext.Value == tier.ConfiguredContext &&
            target.ObservedContext.Value == tier.ConfiguredContext && target.UsableContext.Value == tier.ConfiguredContext - witness.ContextReserve &&
            target.ConfiguredSlots.Value == tier.ConfiguredSlots && target.ObservedSlots.Value == tier.ConfiguredSlots &&
            target.WeightsSha256.Value is not null && target.Quantization.Value is not null && target.ExecutableSha256.Value is not null &&
            target.RuntimeBuild.Value is not null && target.TokenizerSha256.Value is not null && target.TemplateSha256.Value is not null &&
            target.SharedSettingsSha256.Value is not null && target.CachePolicySha256.Value is not null &&
            target.DeadlinePolicySha256.Value is not null && target.AdapterSha256.Value is not null &&
            hardware.PhysicalRamBytes.Value is not null && hardware.PhysicalSwapBytes.Value is not null &&
            hardware.RamLimitBytes.Kind != "unknown" && hardware.RamPlusSwapLimitBytes.Kind != "unknown" &&
            hardware.SwapOnlyLimitBytes.Kind != "unknown" && hardware.CpuModel.Value is not null &&
            hardware.CpuAffinitySha256.Value is not null && hardware.CpuQuota.Kind != "unknown" &&
            hardware.Gpus.Value is { } gpus && gpus.All(static gpu => gpu.VramBytes.Value is > 0);
    }

    private static bool FitsRecord(byte[] bytes)
    {
        if (bytes.Length >= AirTuneRecord.MaxBytes / 4) return false;
        using var document = JsonDocument.Parse(bytes);
        return AirTuneRecord.ValidTokens(document.RootElement);
    }

    private static bool SameIdleResident(TuneWitness? witness, TuneWitness initial, byte[] target) =>
        witness is { Exclusive: true, ExactSettings: true, Active: 0, Queued: 0 } &&
        witness.InstanceId == initial.InstanceId && witness.Generation == initial.Generation && witness.ContextReserve == initial.ContextReserve &&
        JsonSerializer.SerializeToUtf8Bytes(witness.Target, AirTuneRunJsonContext.Default.TuneTarget).AsSpan().SequenceEqual(target);

    private static bool SameStableHardware(TuneHardware? current, byte[] initial)
    {
        if (current is null) return false;
        using var before = JsonDocument.Parse(initial);
        using var after = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(current, AirTuneRunJsonContext.Default.TuneHardware));
        foreach (var field in AirTuneCompare.HardwareMatchFields)
            if (!Matches(field)) return false;
        return Matches("physical_ram_bytes") && Matches("physical_swap_bytes");

        bool Matches(string name) => after.RootElement.TryGetProperty(name, out var observation) &&
            JsonElement.DeepEquals(before.RootElement.GetProperty(name), observation);
    }

    private static async Task<SampleExchange> ReadSampleAsync(ITuneResident lease, Guid request, string model,
        FittedTunePrompt prompt, WorkloadPolicy policy, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(policy.RequestSeconds));
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var observer = new SemanticStreamObserver(Clock, model, expectedChoices: 1, requireExactLength: true);
        var blocks = new SseBlockBuffer(observer: observer);
        try
        {
            idle.Token.ThrowIfCancellationRequested();
            using var response = await lease.StartAsync(request, prompt.Body, idle.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType is not "text/event-stream")
                return Result("generation_refusal");

            await using var stream = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
            var buffer = new byte[16_384];
            TimeSpan? last = null;
            while (true)
            {
                var read = await stream.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                if (read == 0) break;
                observer.ObserveRawByte();
                if (blocks.Append(buffer.AsSpan(0, read)).LimitExceeded)
                    return Result("stream_block_limit");

                var observed = observer.GetExactLengthEvidence().LastSemanticOutput;
                if (observed != last)
                {
                    last = observed;
                    idle.CancelAfter(TimeSpan.FromSeconds(policy.OutputIdleSeconds));
                }
            }

            observer.End(blocks.HasIncompleteBlock ? StreamInputEnd.Torn : StreamInputEnd.Eof);
            idle.Token.ThrowIfCancellationRequested();
            var evidence = observer.Snapshot();
            var exact = observer.GetExactLengthEvidence();
            var matches = exact.Complete && evidence.ModelIdentity == StreamModelIdentity.Matched &&
                evidence.PromptTokens == prompt.Tokens && evidence.CompletionTokens == policy.TargetOutputTokens &&
                exact.Terminator is { } terminal && terminal.Ticks > policy.TargetOutputTokens * TimeSpan.TicksPerSecond / 1_000_000_000L;
            return new(evidence, exact.Terminator, matches ? null : "stream_workload_mismatch");
        }
        catch (OperationCanceledException)
        {
            observer.End(StreamInputEnd.UpstreamCancelledRead);
            return Result(deadline.IsCancellationRequested ? "budget_exhausted" : "output_idle");
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException)
        {
            observer.End(StreamInputEnd.Fault);
            return Result("generation_io");
        }

        SampleExchange Result(string reason) => new(observer.Snapshot(), observer.GetExactLengthEvidence().Terminator, reason);
    }

    private static AirTuneRecord BuildSummary(string runId, TuneWitness resident, WorkloadManifest manifest,
        List<TuneRequestEvidence> workload, List<TuneSampleEvidence> samples, string status)
    {
        TuneFact<string> Warmup(string domain, Func<TuneRequestEvidence, string> select) =>
            Fact(workload.Count == 0 ? null : Fingerprint(manifest.Workload, domain, [select(workload[0])], null));

        var measured = samples.Count > 1;
        return new AirTuneRecord
        {
            SchemaVersion = 1, RunId = runId, InstanceId = Fact(resident.InstanceId), Hardware = resident.Hardware,
            Target = resident.Target, Cap = 1,
            Workload = new TuneWorkload
            {
                Id = manifest.Workload, Generator = Fact(manifest.Generator), GeneratorSha256 = Fact(manifest.GeneratorSha256),
                Seed = Fact(manifest.Seed), WarmupCorpusSha256 = Warmup("warmup-corpus", static row => row.CorpusSha256),
                WarmupBodySha256 = Warmup("warmup-body", static row => row.BodySha256),
            },
            Levels = [new TuneLevel
            {
                Concurrency = 1, Status = measured ? status : "untested",
                CorpusSha256 = Fact(measured ? Fingerprint(manifest.Workload, "corpus", workload.Skip(1).Select(row => row.CorpusSha256), 1) : null),
                BodySha256 = Fact(measured ? Fingerprint(manifest.Workload, "body", workload.Skip(1).Select(row => row.BodySha256), 1) : null),
                Reason = status == "passed" ? null : measured && status == "failed" ? "workload_failure" : "insufficient_evidence",
                WaveOutputTokensPerSecond = status == "passed" ? samples.Skip(1).Select(Rate).ToArray() : null,
            }],
        };
    }

    private static TuneFact<string> Fact(string? value) => new() { Kind = value is null ? "unknown" : "known", Value = value };

    private static decimal Rate(TuneSampleEvidence sample) =>
        Math.Round(sample.CompletionTokens!.Value * (decimal)TimeSpan.TicksPerSecond / sample.TerminalTicks!.Value, 9);

    private static string Fingerprint(string workload, string domain, IEnumerable<string> leaves, int? concurrency)
    {
        var header = $"air-tune/1/{workload}/{domain}\n";
        if (concurrency is { } value) header += value.ToString(CultureInfo.InvariantCulture) + "\n";
        return Digest(Encoding.ASCII.GetBytes(header + string.Join('\n', leaves) + "\n"));
    }

    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed record SampleExchange(SemanticStreamObservation Observation, TimeSpan? Terminal, string? Reason);
}
