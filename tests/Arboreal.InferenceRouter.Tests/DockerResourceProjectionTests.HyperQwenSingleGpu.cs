using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class DockerResourceProjectionTests
{
    private const string Tp1 = "--tensor-parallel-size 1 --disable-custom-all-reduce --max-num-batched-tokens 2048";
    private static readonly (string Name, long Context, int Sequences, string Args)[] HyperQwenSingleGpuTiers =
    [
        ("single-64k", 65536, 1, Tp1),
        ("single-128k-2", 131072, 2, Tp1),
        ("single-max", 163840, 1, Tp1),
    ];

    private const string HyperQwenSwiftSingle64KPayload = $$$"""
        {
          "Image":"{{{HyperQwenLocalId}}}",
          "Cmd":["single"],
          "Env":["HOME=/cache","MODEL=/app/models","PREPARE=0","VERIFY=0","HOST=0.0.0.0","PORT=8000",
            "CTX=fast","SPEC=mtp","DRAFT_TOKENS=4","MAX_LEN=65536","MAX_SEQS=1","GPU_UTIL=0.90",
            "TOOLS=1","PREFIX_CACHE=1","VISION=1","API_SERVERS=1","HF_HUB_OFFLINE=1","TRANSFORMERS_OFFLINE=1",
            "PYTORCH_CUDA_ALLOC_CONF=expandable_segments:False","CUDA_DEVICE_ORDER=PCI_BUS_ID","NCCL_IB_DISABLE=1",
            "EXTRA_ARGS=--tensor-parallel-size 1 --disable-custom-all-reduce --max-num-batched-tokens 2048 --limit-mm-per-prompt {\"image\":{\"count\":8}} --chat-template /app/managed-chat-template.jinja"],
          "Labels":{"arboreal.router":"fixture-router","arboreal.engine":"hyperqwen","arboreal.model":"swift","arboreal.profile":"single-64k"},
          "StopTimeout":60,
          "ExposedPorts":{"8000/tcp":{}},
          "HostConfig":{
            "PortBindings":{"8000/tcp":[{"HostIp":"127.0.0.1","HostPort":"18020"}]},
            "Init":true,
            "RestartPolicy":{"Name":"unless-stopped","MaximumRetryCount":0},
            "LogConfig":{"Type":"","Config":{"max-size":"20m","max-file":"3"}},
            "DeviceRequests":[{"Driver":"","Count":0,"DeviceIDs":["1"],"Capabilities":[["gpu"]]}],
            "Mounts":[
              {"Type":"bind","Source":"/srv/hyperqwen/checkpoints/swift","Target":"/app/models","ReadOnly":true},
              {"Type":"bind","Source":"/srv/hyperqwen/chat-template.jinja","Target":"/app/managed-chat-template.jinja","ReadOnly":true},
              {"Type":"bind","Source":"/srv/hyperqwen/cache/swift/single-64k","Target":"/cache","ReadOnly":false},
              {"Type":"bind","Source":"/srv/hyperqwen/credential","Target":"/app/api_key.txt","ReadOnly":true}
            ],
            "ShmSize":2147483648
          }
        }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HyperQwenSingleGpuProjectsSixTargetsAndReturnsToIdenticalPairedBytes(bool isJson)
    {
        var mixed = LoadHyperQwen(isJson, singleGpu: "1");
        var paired = LoadHyperQwen(isJson);
        using var daemon = new FakeDockerDaemonHandler();
        using var provider = new DockerLifecycleProvider(mixed.Docker!, mixed.Name, daemon);
        using var pairedProvider = new DockerLifecycleProvider(paired.Docker!, paired.Name, daemon);
        using var golden = JsonDocument.Parse(HyperQwenSwiftSingle64KPayload);
        var expected = golden.RootElement;
        var expectedEnv = expected.GetProperty("Env").EnumerateArray().Select(value => value.GetString()).ToArray();
        var expectedHost = expected.GetProperty("HostConfig");
        var expectedMounts = expectedHost.GetProperty("Mounts");
        foreach (var (canonical, alias, family) in HyperQwenModels)
        {
            var model = mixed.Catalog[canonical];
            Assert.Equal("max", model.Profile);
            Assert.Equal(7, model.ServingProfiles.Count);
            var max = model.ServingProfiles.Single(p => p.Name == "max");
            using var before = ProjectHyperQwen(provider, mixed, model, alias, null);
            using var original = ProjectHyperQwen(pairedProvider, paired, paired.Catalog[canonical], canonical, null);
            Assert.Equal(original.RootElement.GetRawText(), before.RootElement.GetRawText());
            if (canonical == "swift") JsonEqual(HyperQwenSwiftMaxPayload, before.RootElement);

            foreach (var (name, context, sequences, _) in HyperQwenSingleGpuTiers)
            {
                var profile = model.ServingProfiles.Single(p => p.Name == name);
                Assert.Equal(context, profile.MaxContext);
                Assert.Equal(sequences, profile.MaxSlots);
                using var result = ProjectHyperQwen(provider, mixed, model, canonical, profile);
                using var aliased = ProjectHyperQwen(provider, mixed, model, alias, profile);
                var actual = result.RootElement;
                Assert.Equal(actual.GetRawText(), aliased.RootElement.GetRawText());
                if (canonical == "swift" && name == "single-64k")
                    JsonEqual(HyperQwenSwiftSingle64KPayload, actual);

                expectedEnv[6] = name == "single-64k" ? "CTX=fast" : "CTX=long";
                expectedEnv[8] = name == "single-64k" ? "DRAFT_TOKENS=4" : "DRAFT_TOKENS=3";
                expectedEnv[9] = FormattableString.Invariant($"MAX_LEN={context}");
                expectedEnv[10] = FormattableString.Invariant($"MAX_SEQS={sequences}");
                Assert.Equal(expectedEnv, actual.GetProperty("Env").EnumerateArray().Select(value => value.GetString()).ToArray());
                Assert.Equal(actual.EnumerateObject().Count(), expected.EnumerateObject().Count());
                foreach (var part in expected.EnumerateObject())
                    if (part.Name is not ("Labels" or "Env" or "HostConfig"))
                        Assert.True(JsonElement.DeepEquals(part.Value, actual.GetProperty(part.Name)), part.Name);
                var labels = actual.GetProperty("Labels");
                Assert.Equal(canonical, labels.GetProperty(DockerLifecycleProvider.LabelModel).GetString());
                Assert.Equal(name, labels.GetProperty(DockerLifecycleProvider.LabelProfile).GetString());
                var host = actual.GetProperty("HostConfig");
                Assert.Equal(expectedHost.EnumerateObject().Count(), host.EnumerateObject().Count());
                foreach (var part in expectedHost.EnumerateObject())
                    if (!part.NameEquals("Mounts"))
                        Assert.True(JsonElement.DeepEquals(part.Value, host.GetProperty(part.Name)), part.Name);
                var mounts = host.GetProperty("Mounts");
                Assert.Equal(4, mounts.GetArrayLength());
                AssertHyperQwenMount(mounts[0], expectedMounts[0], $"/srv/hyperqwen/checkpoints/{family}");
                Assert.True(JsonElement.DeepEquals(expectedMounts[1], mounts[1]));
                AssertHyperQwenMount(mounts[2], expectedMounts[2], HyperQwenCache(family, name));
                Assert.True(JsonElement.DeepEquals(expectedMounts[3], mounts[3]));
            }

            using var after = ProjectHyperQwen(provider, mixed, model, alias, max);
            Assert.Equal(before.RootElement.GetRawText(), after.RootElement.GetRawText());
        }
        Assert.Empty(daemon.Requests);
    }

    private static JsonDocument ProjectHyperQwen(DockerLifecycleProvider provider, EngineDefinition engine,
        EngineModelDefinition model, string spelling, ServingProfile? tier)
    {
        var request = provider.BuildCreateRequest(new LifecycleTarget(engine.Name, spelling, tier, model),
            engine.Docker!.ImageIdentity!);
        return JsonSerializer.SerializeToDocument(request, DockerJsonContext.Default.DockerCreateRequest);
    }
}
