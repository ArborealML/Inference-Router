using System.Net;

namespace Arboreal.InferenceRouter.Tests;

/// <summary>Checks the fixture's observations before using it as a RouterCore witness.</summary>
public sealed class AdmissionFixtureProbeTests
{
    [Fact]
    public async Task FreshWrapperCanDeliverExactBytesWhileResponseIsHeld()
    {
        using var fixtureLifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var responseHeld = new AdmissionFixtureBarrier();
        var sink = new AdmissionFixtureSink(RespondAsync);

        // The engine client can disappear without disposing or invalidating the shared witness.
        using (var retiredDiscovery = new FakeEngineHandler())
        {
            using var retiredWrapper = sink.CreateHandler(retiredDiscovery);
        }

        using var activeDiscovery = new FakeEngineHandler();
        using var activeWrapper = sink.CreateHandler(activeDiscovery);
        using var sender = new HttpMessageInvoker(activeWrapper, disposeHandler: false);
        byte[] payload = """{"model":"test-model","prompt":"open-generation"}"""u8.ToArray();
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://fixture.invalid/v1/completions")
        {
            Content = new ByteArrayContent(payload),
        };

        var exchange = sender.SendAsync(request, fixtureLifetime.Token);
        (int Calls, byte[][] Bodies, bool Completed) observed;
        HttpStatusCode status;
        try
        {
            await responseHeld.Reached.WaitAsync(fixtureLifetime.Token);
            observed = (sink.Calls, sink.ReceivedBodies, exchange.IsCompleted);
        }
        finally
        {
            responseHeld.Release();
            using var response = await exchange;
            status = response.StatusCode;
        }

        Assert.Equal(1, observed.Calls);
        Assert.Equal(payload, Assert.Single(observed.Bodies));
        Assert.False(observed.Completed);
        Assert.Equal(HttpStatusCode.OK, status);

        async Task<HttpResponseMessage> RespondAsync()
        {
            await responseHeld.HoldAsync(fixtureLifetime.Token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
