using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class DockerResourceProjectionTests
{
    private const long DefaultParkingShm = 38_654_705_664L;
    private static readonly (string Name, long Context, int Sequences, string Args)[] HyperQwenParkingTiers =
    [
        ("parked", 262144, 2, LimitedTp2),
        ("64k-4", 65536, 4, LimitedTp2),
        ("64k-6", 65536, 6, LimitedTp2),
        ("64k-8", 65536, 8, LimitedTp2),
        ("96k-4", 98304, 4, LimitedTp2),
        ("128k-4", 131072, 4, LimitedTp2),
    ];

    private const string HyperQwenSwiftParkedPayload = $$$"""
        {
          "Image":"{{{HyperQwenLocalId}}}",
          "Cmd":["single"],
          "Env":["HOME=/cache","MODEL=/app/models","PREPARE=0","VERIFY=0","HOST=0.0.0.0","PORT=8000",
            "CTX=long","SPEC=mtp","DRAFT_TOKENS=3","MAX_LEN=262144","MAX_SEQS=2","GPU_UTIL=0.90",
            "TOOLS=1","PREFIX_CACHE=1","VISION=1","API_SERVERS=1","HF_HUB_OFFLINE=1","TRANSFORMERS_OFFLINE=1",
            "PYTORCH_CUDA_ALLOC_CONF=expandable_segments:False","CUDA_DEVICE_ORDER=PCI_BUS_ID","NCCL_IB_DISABLE=1",
            "KV_OFFLOAD_GB=32","PTH_FA=0","FA2_FP8KV=0","REQ_METRICS=1","REQ_METRICS_DETAILED=0","PREFIX_RETENTION=",
            "EXTRA_ARGS=--tensor-parallel-size 2 --disable-custom-all-reduce --max-num-batched-tokens 2048 --limit-mm-per-prompt {\"image\":{\"count\":8}} --chat-template /app/managed-chat-template.jinja"],
          "Labels":{"arboreal.router":"fixture-router","arboreal.engine":"hyperqwen","arboreal.model":"swift","arboreal.profile":"parked"},
          "StopTimeout":60,
          "ExposedPorts":{"8000/tcp":{}},
          "HostConfig":{
            "PortBindings":{"8000/tcp":[{"HostIp":"127.0.0.1","HostPort":"18020"}]},
            "Init":true,
            "RestartPolicy":{"Name":"unless-stopped","MaximumRetryCount":0},
            "LogConfig":{"Type":"","Config":{"max-size":"20m","max-file":"3"}},
            "DeviceRequests":[{"Driver":"","Count":0,"DeviceIDs":["0","1"],"Capabilities":[["gpu"]]}],
            "Mounts":[
              {"Type":"bind","Source":"/srv/hyperqwen/checkpoints/swift","Target":"/app/models","ReadOnly":true},
              {"Type":"bind","Source":"/srv/hyperqwen/chat-template.jinja","Target":"/app/managed-chat-template.jinja","ReadOnly":true},
              {"Type":"bind","Source":"/srv/hyperqwen/parking-cache/swift","Target":"/cache","ReadOnly":false},
              {"Type":"bind","Source":"/srv/hyperqwen/credential","Target":"/app/api_key.txt","ReadOnly":true}
            ],
            "ShmSize":38654705664
          }
        }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HyperQwenParkingShmProjectsTwelveDefaultsAndPreservesPairedReturnBytes(bool isJson)
    {
        var engine = LoadHyperQwen(isJson, singleGpu: "1", parkingCacheGiB: 32);
        var unparked = LoadHyperQwen(isJson, singleGpu: "1");
        using var daemon = new FakeDockerDaemonHandler();
        using var provider = new DockerLifecycleProvider(engine.Docker!, engine.Name, daemon);
        using var unparkedProvider = new DockerLifecycleProvider(unparked.Docker!, unparked.Name, daemon);
        using var golden = JsonDocument.Parse(HyperQwenSwiftParkedPayload);
        var expected = golden.RootElement;
        var env = expected.GetProperty("Env").EnumerateArray().Select(value => value.GetString()).ToArray();
        var expectedHost = expected.GetProperty("HostConfig");
        var expectedMounts = expectedHost.GetProperty("Mounts");
        foreach (var (canonical, alias, family) in HyperQwenModels)
        {
            var model = engine.Catalog[canonical];
            Assert.Equal("max", model.Profile);
            Assert.Equal(13, model.ServingProfiles.Count);
            var max = model.ServingProfiles.Single(p => p.Name == "max");
            using var before = ProjectHyperQwen(provider, engine, model, alias, null);
            using var earlier = ProjectHyperQwen(unparkedProvider, unparked, unparked.Catalog[canonical], canonical, null);
            Assert.Equal(earlier.RootElement.GetRawText(), before.RootElement.GetRawText());
            if (canonical == "swift")
            {
                JsonEqual(HyperQwenSwiftMaxPayload, before.RootElement);
                using var single = ProjectHyperQwen(provider, engine, model, canonical,
                    model.ServingProfiles.Single(p => p.Name == "single-64k"));
                JsonEqual(HyperQwenSwiftSingle64KPayload, single.RootElement);
            }

            foreach (var (name, context, sequences, _) in HyperQwenParkingTiers)
            {
                var tier = model.ServingProfiles.Single(p => p.Name == name);
                Assert.Equal(context, tier.MaxContext);
                Assert.Equal(sequences, tier.MaxSlots);
                using var result = ProjectHyperQwen(provider, engine, model, canonical, tier);
                using var aliased = ProjectHyperQwen(provider, engine, model, alias, tier);
                var actual = result.RootElement;
                Assert.Equal(actual.GetRawText(), aliased.RootElement.GetRawText());
                if (canonical == "swift" && name == "parked") JsonEqual(HyperQwenSwiftParkedPayload, actual);

                env[9] = FormattableString.Invariant($"MAX_LEN={context}");
                env[10] = FormattableString.Invariant($"MAX_SEQS={sequences}");
                Assert.Equal(env, actual.GetProperty("Env").EnumerateArray().Select(value => value.GetString()).ToArray());
                Assert.Equal(expected.EnumerateObject().Count(), actual.EnumerateObject().Count());
                foreach (var part in expected.EnumerateObject())
                    if (part.Name is not ("Labels" or "Env" or "HostConfig"))
                        Assert.True(JsonElement.DeepEquals(part.Value, actual.GetProperty(part.Name)), part.Name);
                var labels = actual.GetProperty("Labels");
                Assert.Equal(canonical, labels.GetProperty(DockerLifecycleProvider.LabelModel).GetString());
                Assert.Equal(name, labels.GetProperty(DockerLifecycleProvider.LabelProfile).GetString());
                var host = actual.GetProperty("HostConfig");
                Assert.Equal(DefaultParkingShm, host.GetProperty("ShmSize").GetInt64());
                Assert.Equal(expectedHost.EnumerateObject().Count(), host.EnumerateObject().Count());
                foreach (var part in expectedHost.EnumerateObject())
                    if (!part.NameEquals("Mounts"))
                        Assert.True(JsonElement.DeepEquals(part.Value, host.GetProperty(part.Name)), part.Name);
                var mounts = host.GetProperty("Mounts");
                Assert.Equal(4, mounts.GetArrayLength());
                AssertHyperQwenMount(mounts[0], expectedMounts[0], $"/srv/hyperqwen/checkpoints/{family}");
                Assert.True(JsonElement.DeepEquals(expectedMounts[1], mounts[1]));
                AssertHyperQwenMount(mounts[2], expectedMounts[2], $"/srv/hyperqwen/parking-cache/{family}");
                Assert.True(JsonElement.DeepEquals(expectedMounts[3], mounts[3]));
            }
            using var after = ProjectHyperQwen(provider, engine, model, alias, max);
            Assert.Equal(before.RootElement.GetRawText(), after.RootElement.GetRawText());
        }
        Assert.Empty(daemon.Requests);
    }

    [Theory]
    [InlineData(1, 1, 5_368_709_120L)]
    [InlineData(48, 8, 55_834_574_848L)]
    public void HyperQwenParkingShmProjectsCacheEndpointsWithVariableAndFixedSequences(int cache, int parkedSequences, long shm)
    {
        var engine = LoadHyperQwen(isJson: false, singleGpu: "1", parkingCacheGiB: cache, parkedSequences: parkedSequences);
        var model = engine.Catalog["swift"];
        using var daemon = new FakeDockerDaemonHandler();
        using var provider = new DockerLifecycleProvider(engine.Docker!, engine.Name, daemon);
        foreach (var (name, _, fixedSequences, _) in HyperQwenParkingTiers)
        {
            var tier = model.ServingProfiles.Single(p => p.Name == name);
            var sequences = name == "parked" ? parkedSequences : fixedSequences;
            Assert.Equal(sequences, tier.MaxSlots);
            var request = provider.BuildCreateRequest(new LifecycleTarget(engine.Name, "swift", tier, model),
                engine.Docker!.ImageIdentity!);
            Assert.Equal(shm, request.HostConfig!.ShmSize);
            Assert.Contains(FormattableString.Invariant($"KV_OFFLOAD_GB={cache}"), request.Env!);
            Assert.Contains(FormattableString.Invariant($"MAX_SEQS={sequences}"), request.Env!);
        }
        Assert.Empty(daemon.Requests);
    }
}
