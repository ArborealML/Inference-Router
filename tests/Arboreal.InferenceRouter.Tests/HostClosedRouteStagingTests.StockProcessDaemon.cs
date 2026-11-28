using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Arboreal.InferenceRouter.Lifecycle;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class HostClosedRouteStagingTests
{
    // The disposable daemon is the only simulated Docker control. The actual stock ingress
    // is its writer and the shipped observer corroborates its bounded HTTP census.
    private sealed class StockDaemon(StockSeed seed, StockBackend initial, CheckedEnrolledTarget first,
        CheckedEnrolledTarget next, string terminal, string release, string receipts,
        Func<ServingBrokerSnapshot?> readBroker, EnrolledSuccessorFileJournal journal) : IAsyncDisposable
    {
        private static readonly string[] BeforeCreate = ["stop", "vacant"];
        private readonly Lock _state = new();
        private readonly List<string> _effects = [];
        private StockResident? _resident = new(seed.Initial, first, initial);
        private WebApplication? _writer;
        private WebApplication? _reader;
        internal StockBackend? Successor { get; private set; }
        internal string? CreatedBytesDigest { get; private set; }
        internal Guid? StopOperation { get; private set; }

        internal string[] Effects
        {
            get { lock (_state) return [.. _effects]; }
        }

        internal async Task StartAsync(string writeSocket, string readSocket, CancellationToken ct)
        {
            if (_writer is not null || _reader is not null) throw new InvalidOperationException("Start the fixture once.");
#pragma warning disable IDISP003 // Each daemon fixture starts once and disposes both private endpoints.
            _writer = Build(writeSocket, allowsEffects: true);
            _reader = Build(readSocket, allowsEffects: false);
#pragma warning restore IDISP003
            await _writer.StartAsync(ct);
            await _reader.StartAsync(ct);
            StockSetMode(writeSocket, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherWrite);
            StockSetMode(readSocket, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherWrite);
        }

        private WebApplication Build(string path, bool allowsEffects)
        {
            var builder = WebApplication.CreateSlimBuilder([]);
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server => server.ListenUnixSocket(path));
            var app = builder.Build();
            app.Run(ctx => HandleAsync(ctx, allowsEffects));
            return app;
        }

        private async Task HandleAsync(HttpContext ctx, bool allowsEffects)
        {
            var path = ctx.Request.Path.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (path.Length < 3 || path[0] != first.ApiVersion || path[0] != next.ApiVersion)
            {
                ctx.Response.StatusCode = 404;
                return;
            }

            if (HttpMethods.IsGet(ctx.Request.Method))
            {
                await ReadAsync(ctx, path);
                return;
            }
            if (!allowsEffects || !HttpMethods.IsPost(ctx.Request.Method))
            {
                ctx.Response.StatusCode = 405;
                return;
            }

            var installed = HostEffectCheckpoint.ReadInstalledCommitment(seed.CheckpointPath);
            var broker = Assert.IsType<ServingBrokerSnapshot>(readBroker());
            var cutoff = Assert.IsType<ServingBrokerClose>(broker.Record.Close);
            Assert.True(cutoff.Conclusive);
            Assert.False(broker.Record.Open);
            Assert.DoesNotContain(broker.Record.Uses, use => use.Phase is ServingBrokerUsePhase.Issued or ServingBrokerUsePhase.Reserved);
            if (path is [_, "containers", var old, "stop"])
            {
                Assert.Equal(seed.Initial.InstanceId, old);
                Assert.Equal(HostCheckpointPhase.StopDispatching, installed.State.Phase);
                Assert.Equal(cutoff.Retirement.Stop, installed.State.Transition?.Stop);
                await initial.StopAsync();
                lock (_state)
                {
                    _resident = null;
                    StopOperation = cutoff.Retirement.Stop.Id;
                    _effects.Add("stop");
                }
                ctx.Response.StatusCode = 204;
                return;
            }

            Assert.Equal(HostCheckpointPhase.LaunchDispatching, installed.State.Phase);
            var launch = Assert.IsType<HostMutation>(installed.State.Transition?.Launch);
            Assert.Equal(next.Plan.Digest(), launch.PlanDigest);
            if (path is [_, "containers", "create"])
            {
                using var buffer = new MemoryStream();
                await ctx.Request.Body.CopyToAsync(buffer, ctx.RequestAborted);
                var raw = buffer.ToArray();
                Assert.Equal(Convert.FromBase64String(next.Intent.CreateBytes), raw);
                var effect = new HostCheckpointLaunchEffect(launch, next.Plan, new string('B', 64));
                lock (_state)
                {
                    Assert.Null(_resident);
                    Assert.Equal(BeforeCreate, _effects);
                    CreatedBytesDigest = Convert.ToHexString(SHA256.HashData(raw));
                    _resident = new(effect, next, Backend: null);
                    _effects.Add("create");
                }
                ctx.Response.StatusCode = 201;
                await ReplyAsync(ctx, new JsonObject { ["Id"] = effect.InstanceId });
                return;
            }

            if (path is [_, "containers", var created, "start"])
            {
                StockResident pending;
                lock (_state)
                {
                    pending = Assert.IsType<StockResident>(_resident);
                    Assert.Equal(pending.Effect.InstanceId, created);
                    Assert.Null(pending.Backend);
                }
                var started = await StockBackend.StartAsync(next, 4, terminal, release, receipts, ctx.RequestAborted);
                started.RequireKernelWitness(BackendUid);
#pragma warning disable IDISP003 // Only one anchored successor can start in this two-resident fixture.
                Successor = started;
#pragma warning restore IDISP003
                lock (_state)
                {
                    _resident = pending with { Backend = started };
                    _effects.Add("start");
                }
                ctx.Response.StatusCode = 204;
                return;
            }

            ctx.Response.StatusCode = 404;
        }

        private async Task ReadAsync(HttpContext ctx, string[] path)
        {
            StockResident? resident;
            lock (_state) resident = _resident;
            if (path is [_, "containers", "json"])
            {
                var rows = new JsonArray();
                if (resident is not null)
                    rows.Add((JsonNode)new JsonObject { ["Id"] = resident.Effect.InstanceId, ["State"] = resident.State,
                        ["Labels"] = ProjectCreate(resident.Target)["Labels"]!.DeepClone() });
                await ReplyAsync(ctx, rows);
                return;
            }

            if (path is [_, "images", var id, "json"])
            {
                var image = new[] { first.Image, next.Image }.SingleOrDefault(candidate => candidate.Id == id);
                if (image is null)
                {
                    ctx.Response.StatusCode = 404;
                    return;
                }
                await ReplyAsync(ctx, new JsonObject { ["Id"] = image.Id, ["RepoDigests"] = new JsonArray(image.RepoDigest),
                    ["Os"] = "linux", ["Architecture"] = "amd64" });
                return;
            }

            if (path is [_, "containers", var instance, "json"] && resident?.Effect.InstanceId == instance)
            {
                var create = ProjectCreate(resident.Target);
                await ReplyAsync(ctx, new JsonObject { ["Id"] = instance, ["Image"] = resident.Target.Image.Id,
                    ["Config"] = create, ["HostConfig"] = create["HostConfig"]!.DeepClone(),
                    ["State"] = new JsonObject { ["Status"] = resident.State, ["Running"] = resident.Backend is not null,
                        ["Paused"] = false, ["Restarting"] = false, ["Dead"] = false,
                        ["Pid"] = resident.Backend?.Identity.Handler.ProcessId ?? 0 } });
                return;
            }

            ctx.Response.StatusCode = 404;
        }

        internal ValueTask<SuccessorHeldPartition?> ReadHeldAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var broker = readBroker()?.Record;
            if (broker is null) return ValueTask.FromResult<SuccessorHeldPartition?>(null);
            var installed = HostEffectCheckpoint.ReadInstalledCommitment(seed.CheckpointPath);
            StockResident? resident;
            lock (_state)
            {
                resident = _resident;
                if (resident is null && installed.State.Phase == HostCheckpointPhase.StopApplied && !_effects.Contains("vacant"))
                    _effects.Add("vacant");
            }

            HeldServingGeneration? serving = null;
            if (resident is { Backend: { } backend } &&
                broker.Binding.Applied.State.AppliedLaunch == resident.Effect)
                serving = backend.Serving(broker.Binding);
            else if (resident is { Backend: { } successor } && installed.State.AppliedLaunch == resident.Effect &&
                journal.ReadAppliedDigestForServing(resident.Effect) is { } digest)
                serving = successor.Serving(successor.Binding(seed.Scope, installed, resident.Target,
                    broker.Binding.Route.RouteIncarnation + 1, successor.Profile.Slots, digest));

            var ingress = broker.Close?.Retirement.IngressGeneration ?? broker.Binding.Route.RouteIncarnation;
            return ValueTask.FromResult<SuccessorHeldPartition?>(new(seed.Scope.Reservation, seed.Scope.Commission.Head,
                Revision: installed.State.Revision, OwnerGeneration: 1, CoverageGeneration: 1, ingress,
                IngressClosed: !broker.Open, seed.Scope.Commission.Devices, resident?.Effect, serving?.Exclusion.Binding.Route,
                resident?.Target.Image, resident?.Backend is not null, resident?.Target.Plan.BackendRoute, resident?.Target.Endpoint,
                PreflightTarget: next.Key, Serving: serving));
        }

        private static JsonObject ProjectCreate(CheckedEnrolledTarget target) =>
            Assert.IsType<JsonObject>(JsonNode.Parse(Convert.FromBase64String(target.Intent.CreateBytes)));

        private static Task ReplyAsync(HttpContext ctx, JsonNode json)
        {
            ctx.Response.ContentType = "application/json";
            return ctx.Response.WriteAsync(json.ToJsonString(), ctx.RequestAborted);
        }

        public async ValueTask DisposeAsync()
        {
            if (_reader is not null) await _reader.DisposeAsync();
            if (_writer is not null) await _writer.DisposeAsync();
            if (Successor is not null) await Successor.DisposeAsync();
        }

        private sealed record StockResident(HostCheckpointLaunchEffect Effect, CheckedEnrolledTarget Target, StockBackend? Backend)
        {
            internal string State => Backend is null ? "created" : "running";
        }
    }
}
