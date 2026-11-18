using Arboreal.InferenceRouter.Lifecycle;

if (args.Length != 1) return 2;

using var reservation = HostReservation.Open(args[0]);
var epoch = reservation.Grant("crashed-owner");
reservation.Begin(epoch, HostMutationKind.Launch, "crashed-owner", "original.model");
Console.WriteLine("recorded");
// The parent keeps stdin open and terminates this process while the intent remains pending.
_ = Console.In.Read();
return 0;
