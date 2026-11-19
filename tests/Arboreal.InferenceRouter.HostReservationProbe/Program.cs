using Arboreal.InferenceRouter;
using Arboreal.InferenceRouter.Lifecycle;

if (args is ["--effect-ledger", var ledgerPath, var ledgerMode, var ledgerTarget])
{
    Console.WriteLine(new HostEffectLedgerProbe(ledgerPath).Run(ledgerMode, ledgerTarget));
    return 0;
}

if (args is ["--checkpoint-replace", var checkpointPath])
{
    using var checkpoint = HostEffectCheckpoint.Open(checkpointPath, (temporary, installed) =>
    {
        Console.WriteLine("before-replacement");
        _ = Console.In.ReadLine();
        File.Move(temporary, installed, overwrite: true);
        Console.WriteLine("after-replacement");
        _ = Console.In.ReadLine();
    });
    checkpoint.PrepareInitialLaunch(new HostCheckpointPlan("a", "a.model", null, "test-config", 1000, "route-a",
        new string('A', 64)));
    return 0;
}

if (args.Length == 3)
{
    var path = args[0];
    var mode = args[1];
    var worker = new RestartHostWorker(Path.GetDirectoryName(path)!, mode);
    var engine = args[2];
    var model = $"{engine}.model";
    var target = new LifecycleTarget(engine, model, null, new EngineModelDefinition { Name = model, Profile = "default" });
    HostSwitchPublication? publication = null;
    if (mode == "published-before-ack")
    {
        HostServingProof? published = null;
        publication = new HostSwitchPublication(proof =>
        {
            published = proof;
            return true;
        }, () =>
        {
            // At this point CommitVerifiedRoute succeeded, but the caller has no switch reply.
            Console.WriteLine($"published:{published!.ContainerId}");
            _ = Console.In.Read();
        });
    }
    var result = await new HostLifecycleCoordinator(path, worker).SwitchAsync(target, CancellationToken.None, publication);
    Console.WriteLine(result.Reason ?? "ready");
    return 0;
}

if (args.Length != 1) return 2;
using var reservation = HostReservation.Open(args[0]);
var epoch = reservation.Grant("crashed-owner");
var operation = reservation.Begin(epoch, HostMutationKind.Launch, "crashed-owner", "original.model",
    planDigest: new string('A', 64));
reservation.MarkDispatching(operation);
Console.WriteLine("recorded");
// The parent keeps stdin open and terminates this process while the intent remains pending.
_ = Console.In.Read();
return 0;
