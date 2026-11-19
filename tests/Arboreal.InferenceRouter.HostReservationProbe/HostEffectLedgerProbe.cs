using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter;

// Separate journal roles in a cooperative fixture, which can all run in one process. The worker
// issue fence spans the decision and daemon entry, including the exact owner/vacancy comparison.
internal sealed class HostEffectLedgerProbe(string checkpointPath)
{
    private const string Group = "fixture-group";
    private const string Enrollment = "fixture-enrollment";
    private const string WrongId = "wrong-instance";
    private static readonly HostEffectLedgerJsonContext Json = HostEffectLedgerJsonContext.Default;
    private readonly string _checkpointPath = checkpointPath;
    private readonly string _root = Path.GetDirectoryName(checkpointPath)!;
    private string FencePath => Path.Combine(_root, "worker.fence");
    private string DaemonPath => Path.Combine(_root, "daemon-residency.json");

    public string Run(string mode, string target)
    {
        try { return RunCommand(mode, target); }
        catch (Exception error) when (error is IOException or JsonException)
        {
            return "pending";
        }
    }

    private string RunCommand(string mode, string target)
    {
        switch (mode)
        {
            case "initialize":
                using (var fence = new FileStream(FencePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                    Publish(DaemonPath, new LedgerResidency(), Json.LedgerResidency, "residency", mode);
                return "initialized";
            case "reject":
            case "pause-rejection":
            case "pause-never-issue":
                return Reject(Guid.Parse(target), mode);
            case "deliver":
                var accepted = ReadOperation("acceptances", Guid.Parse(target), Json.LedgerCommand)
                    ?? throw new InvalidOperationException("No such accepted operation.");
                return Submit(accepted, mode).Disposition;
            default:
                return Switch(mode, target);
        }
    }

    private string Switch(string mode, string engine)
    {
        using var checkpoint = HostEffectCheckpoint.Open(_checkpointPath);
        var state = checkpoint.State;
        if (state.Phase is HostCheckpointPhase.StopDispatching or HostCheckpointPhase.LaunchDispatching)
        {
            var command = Command(state);
            var replacement = mode switch
            {
                "corrected-reply" => OfferCorrectedReply(command),
                "weak-not-found" => new HostMutationOutcome(command.Operation, HostMutationStatus.UnknownAfterIssue),
                _ => null,
            };
            if (!Reconcile(checkpoint, replacement)) return "pending";
            return checkpoint.State.Phase == HostCheckpointPhase.StopApplied
                ? StartLaunch(checkpoint, mode) : Describe(checkpoint.State);
        }
        if (state.Phase == HostCheckpointPhase.StopApplied) return StartLaunch(checkpoint, mode);
        if (state.Phase == HostCheckpointPhase.Genesis)
        {
            checkpoint.PrepareInitialLaunch(Plan(engine));
            return StartLaunch(checkpoint, mode);
        }

        var incumbent = state.AppliedLaunch ?? throw new InvalidOperationException("No resident to switch.");
        if (incumbent.Plan == Plan(engine)) return Describe(state);
        if (!MatchesResident(ReadResidency(), Group, Enrollment, incumbent)) return "pending";
        var stop = checkpoint.PrepareSuccessor(Plan(engine));
        checkpoint.MarkStopDispatching(stop);
        if (mode == "changed-owner")
        {
            using var fence = OpenFence();
            var actual = ReadResidency();
            ReplaceResidency(actual with { Resident = actual.Resident! with { Enrollment = "other-owner" } }, mode);
        }
        var reply = Submit(Command(checkpoint.State), mode).Outcome;
        if (!Reconcile(checkpoint, reply)) return "pending";
        return checkpoint.State.Phase == HostCheckpointPhase.StopApplied
            ? StartLaunch(checkpoint, mode) : Describe(checkpoint.State);
    }

    private string StartLaunch(HostEffectCheckpoint checkpoint, string mode)
    {
        if (ReadResidency() is not { Resident: null, Foreign: false }) return "pending";
        var state = checkpoint.State;
        var launch = state.Phase == HostCheckpointPhase.StopApplied
            ? checkpoint.PrepareSuccessorLaunch(state.Transition!.Plan) : state.Transition!.Launch!;
        checkpoint.MarkLaunchDispatching(launch);
        if (mode == "changed-vacancy")
        {
            using var fence = OpenFence();
            ReplaceResidency(new LedgerResidency(Foreign: true), mode);
        }
        var reply = Submit(Command(checkpoint.State), mode).Outcome;
        return Reconcile(checkpoint, reply) ? Describe(checkpoint.State) : "pending";
    }

    private bool Reconcile(HostEffectCheckpoint checkpoint, HostMutationOutcome? returned = null)
    {
        var command = Command(checkpoint.State);
        var operation = command.Operation;
        var decision = ReadOperation("decisions", operation.Id, Json.LedgerDecision);
        if (decision is null || decision.Command != command ||
            ReadOperation("acceptances", operation.Id, Json.LedgerCommand) != command) return false;
        var terminal = ReadOperation("terminals", operation.Id, Json.LedgerTerminal);
        if (terminal is null && decision.NeverIssue) terminal = RecoverRejection(decision);
        if (terminal is null || terminal.Command != command) return false;
        var reply = returned ?? terminal.Outcome;
        if (reply.Operation != operation) return false;
        if (decision.NeverIssue)
        {
            if (operation.Kind != HostMutationKind.Stop || reply != terminal.Outcome ||
                reply.Status != HostMutationStatus.RejectedBeforeIssue || reply.ContainerId is not null) return false;
            checkpoint.RetainAfterRejectedStop(reply, new HostCheckpointBeforeIssueFence(decision.Command.Operation));
            return true;
        }

        var effect = ReadOperation("effects", operation.Id, Json.LedgerEffect);
        if (reply.Status != HostMutationStatus.Applied || effect is null || effect.Command != command) return false;
        if (operation.Kind == HostMutationKind.Launch && effect.Launch is { } launched)
        {
            if (launched.Operation != operation || launched.Plan != command.Plan || !launched.IsValid() ||
                launched.InstanceId != reply.ContainerId || terminal.Outcome != reply ||
                !MatchesResident(ReadResidency(), command.Group, command.Enrollment, launched))
                return false;
            checkpoint.RecordAppliedLaunch(reply, launched);
            return true;
        }
        if (operation.Kind != HostMutationKind.Stop || effect.Stop is not { } stopped || command.Incumbent is not { } incumbent ||
            !stopped.Matches(operation, incumbent) || stopped.InstanceId != reply.ContainerId || terminal.Outcome != reply) return false;
        checkpoint.RecordAppliedStop(reply, stopped);
        return true;
    }

    private LedgerSubmission Submit(LedgerCommand command, string mode)
    {
        var id = command.Operation.Id;
        Publish(Path.Combine(_root, "sends", $"{id:N}.{Guid.NewGuid():N}.json"), command, Json.LedgerCommand, "sends", mode);
        using (var fence = TryOpenFence())
        {
            if (fence is null) return new LedgerSubmission("worker-busy");
            if (!Accept(command, mode)) return new LedgerSubmission("binding-conflict");
        }
        Park(mode, "accepted");

        LedgerEffect effect;
        using (var fence = TryOpenFence())
        {
            if (fence is null) return new LedgerSubmission("worker-busy");
            if (ReadOperation("decisions", id, Json.LedgerDecision) is { } decided)
            {
                if (decided.Command != command) return new LedgerSubmission("binding-conflict");
                var remembered = ReadOperation("terminals", id, Json.LedgerTerminal);
                return new LedgerSubmission(decided.NeverIssue ? "barred" : "already-issued", remembered?.Outcome);
            }
            var actual = ReadResidency();
            if (!CanIssue(command, actual)) return new LedgerSubmission("authority-refused");
            WriteOperation("decisions", id, new LedgerDecision(command, NeverIssue: false), Json.LedgerDecision, mode);
            Park(mode, "authorized");
            effect = ApplyInDaemon(command, actual, mode);
        }
        Park(mode, "applied");

        var actualId = effect.Launch?.InstanceId ?? effect.Stop!.InstanceId;
        var terminalId = mode == "wrong-terminal" ? WrongId : actualId;
        var terminal = new HostMutationOutcome(command.Operation, HostMutationStatus.Applied, terminalId);
        WriteOperation("terminals", id, new LedgerTerminal(command, terminal), Json.LedgerTerminal, mode);
        Park(mode, "terminal");
        return new LedgerSubmission("issued", mode == "wrong-reply" ? terminal with { ContainerId = WrongId } : terminal);
    }

    private bool Accept(LedgerCommand command, string mode)
    {
        var id = command.Operation.Id;
        if (ReadOperation("acceptances", id, Json.LedgerCommand) is { } accepted) return accepted == command;
        if (command.Group != Group || command.Enrollment != Enrollment) return false;
        WriteOperation("acceptances", id, command, Json.LedgerCommand, mode);
        return true;
    }

    private string Reject(Guid id, string mode)
    {
        using var fence = TryOpenFence();
        if (fence is null) return "worker-busy";
        var command = ReadOperation("acceptances", id, Json.LedgerCommand);
        if (command is not { Operation.Kind: HostMutationKind.Stop }) return "unproven";
        if (ReadOperation("decisions", id, Json.LedgerDecision) is { } decided)
        {
            if (decided.Command != command) return "unproven";
            if (!decided.NeverIssue) return "issue-won";
            return FinishRejection(decided, mode) is not null ? "certified" : "pending";
        }
        Park(mode, "rejection");
        var rejection = new LedgerDecision(command, NeverIssue: true);
        WriteOperation("decisions", id, rejection, Json.LedgerDecision, mode);
        Park(mode, "never-issue");
        return FinishRejection(rejection, mode) is not null ? "certified" : "pending";
    }

    private LedgerTerminal? RecoverRejection(LedgerDecision expected)
    {
        using var fence = TryOpenFence();
        var id = expected.Command.Operation.Id;
        if (fence is null || ReadOperation("decisions", id, Json.LedgerDecision) != expected ||
            ReadOperation("acceptances", id, Json.LedgerCommand) != expected.Command) return null;
        return FinishRejection(expected, "recover");
    }

    // Called under the worker fence. Only the independently stored never-issue choice can recreate
    // its canonical terminal. A conflicting or corrupt installed terminal is never overwritten.
    private LedgerTerminal? FinishRejection(LedgerDecision decision, string mode)
    {
        var command = decision.Command;
        var id = command.Operation.Id;
        var canonical = new LedgerTerminal(command, new HostMutationOutcome(command.Operation, HostMutationStatus.RejectedBeforeIssue));
        if (ReadOperation("terminals", id, Json.LedgerTerminal) is { } installed)
            return installed == canonical ? installed : null;
        WriteOperation("terminals", id, canonical, Json.LedgerTerminal, mode);
        return canonical;
    }

    private HostMutationOutcome? OfferCorrectedReply(LedgerCommand command)
    {
        var effect = ReadOperation("effects", command.Operation.Id, Json.LedgerEffect);
        return effect?.Launch is { } launch
            ? new HostMutationOutcome(command.Operation, HostMutationStatus.Applied, launch.InstanceId) : null;
    }

    private static bool CanIssue(LedgerCommand command, LedgerResidency actual)
    {
        if (actual.Foreign) return false;
        var operation = command.Operation;
        if (operation.Kind == HostMutationKind.Launch)
            return actual.Resident is null && command.Plan.Matches(operation, operation.Epoch);
        return operation.Kind == HostMutationKind.Stop && command.Incumbent is { } incumbent &&
            MatchesResident(actual, command.Group, command.Enrollment, incumbent) && incumbent.Plan == command.Plan &&
            operation.Target == incumbent.InstanceId && operation.Engine == incumbent.Operation.Engine &&
            operation.Epoch > incumbent.Operation.Epoch;
    }

    private static bool MatchesResident(LedgerResidency actual, string group, string enrollment, HostCheckpointLaunchEffect launch) =>
        !actual.Foreign && actual.Resident is { } resident && resident.Group == group &&
        resident.Enrollment == enrollment && resident.Effect == launch;

    private LedgerEffect ApplyInDaemon(LedgerCommand command, LedgerResidency actual, string mode)
    {
        var operation = command.Operation;
        WriteOperation("attempts", operation.Id, command, Json.LedgerCommand, mode);
        Park(mode, "attempt");
        LedgerEffect effect;
        if (operation.Kind == HostMutationKind.Launch)
        {
            var launched = new HostCheckpointLaunchEffect(operation, command.Plan, $"daemon-{Guid.NewGuid():N}");
            ReplaceResidency(new LedgerResidency(new LedgerResident(command.Group, command.Enrollment, launched)), mode);
            effect = new LedgerEffect(command, Launch: launched);
        }
        else
        {
            var stopped = actual.Resident!.Effect;
            ReplaceResidency(new LedgerResidency(), mode);
            effect = new LedgerEffect(command, Stop: new HostCheckpointStopEffect(operation, stopped.Operation, stopped.InstanceId));
        }
        Park(mode, "effect");
        WriteOperation("effects", operation.Id, effect, Json.LedgerEffect, mode);
        return effect;
    }

    private static LedgerCommand Command(HostEffectCheckpointState state)
    {
        var transition = state.Transition!;
        if (state.Phase != HostCheckpointPhase.StopDispatching)
            return new LedgerCommand(Group, Enrollment, transition.Launch!, transition.Plan);

        var incumbent = state.AppliedLaunch!;
        return new LedgerCommand(Group, Enrollment, transition.Stop!, incumbent.Plan, incumbent);
    }

    private static HostCheckpointPlan Plan(string engine) =>
        new(engine, $"{engine}.model", "named", $"{engine}-config", 1500, $"{engine}-route", new string('A', 64));

    private static string Describe(HostEffectCheckpointState state) => state.Phase == HostCheckpointPhase.RetainedAfterRejection
        ? "retained" : $"resident:{state.AppliedLaunch!.Operation.Engine}";

    private static void Park(string mode, string step)
    {
        if (mode != $"pause-{step}") return;
        Console.WriteLine(step);
        if (Console.ReadLine() != "continue") throw new InvalidOperationException("Fixture gate closed without release.");
    }

    private LedgerResidency ReadResidency() => Read(DaemonPath, Json.LedgerResidency)
        ?? throw new IOException("The fake daemon has no provisioned residency record.");

    private void ReplaceResidency(LedgerResidency next, string mode) =>
        Publish(DaemonPath, next, Json.LedgerResidency, "residency", mode, replace: true);

    private FileStream OpenFence() => new(FencePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    private FileStream? TryOpenFence()
    {
        try { return OpenFence(); }
        catch (IOException) { return null; }
    }

    private T? ReadOperation<T>(string journal, Guid id, JsonTypeInfo<T> type) where T : class =>
        Read(Path.Combine(_root, journal, $"{id:N}.json"), type);

    private void WriteOperation<T>(string journal, Guid id, T value, JsonTypeInfo<T> type, string mode) =>
        Publish(Path.Combine(_root, journal, $"{id:N}.json"), value, type, journal, mode);

    private static T? Read<T>(string path, JsonTypeInfo<T> type) where T : class
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return JsonSerializer.Deserialize(file, type) ?? throw new IOException("A published ledger record was empty.");
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void Publish<T>(string path, T value, JsonTypeInfo<T> type, string boundary, string mode, bool replace = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.new";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, type);
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var cut = 0;
            var append = $"append-{boundary}";
            if (mode == $"pause-{append}")
            {
                cut = bytes.Length / 2;
                file.Write(bytes.AsSpan(0, cut));
                file.Flush(flushToDisk: true);
                Park(mode, append);
            }
            file.Write(bytes.AsSpan(cut));
            file.Flush(flushToDisk: true);
        }
        Park(mode, $"publish-{boundary}");
        File.Move(temporary, path, overwrite: replace);
    }
}

internal sealed record LedgerCommand(string Group, string Enrollment, HostMutation Operation, HostCheckpointPlan Plan,
    HostCheckpointLaunchEffect? Incumbent = null);
internal sealed record LedgerDecision(LedgerCommand Command, bool NeverIssue);
internal sealed record LedgerTerminal(LedgerCommand Command, HostMutationOutcome Outcome);
internal sealed record LedgerEffect(LedgerCommand Command, HostCheckpointLaunchEffect? Launch = null,
    HostCheckpointStopEffect? Stop = null);
internal sealed record LedgerResident(string Group, string Enrollment, HostCheckpointLaunchEffect Effect);
internal sealed record LedgerResidency([property: JsonRequired] LedgerResident? Resident = null,
    [property: JsonRequired] bool Foreign = false);
internal readonly record struct LedgerSubmission(string Disposition, HostMutationOutcome? Outcome = null);

[JsonSerializable(typeof(LedgerCommand))]
[JsonSerializable(typeof(LedgerDecision))]
[JsonSerializable(typeof(LedgerTerminal))]
[JsonSerializable(typeof(LedgerEffect))]
[JsonSerializable(typeof(LedgerResidency))]
internal sealed partial class HostEffectLedgerJsonContext : JsonSerializerContext;
