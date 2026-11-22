using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Arboreal.InferenceRouter.Tests;

public sealed class CliOutputProcessTests
{
    [Theory(Timeout = 30_000)]
    [InlineData("models", "/v1/models", 200, """{"data":[{"id":"example","owned_by":"cpu","context_window":32768}]}""", "example  cpu  32768")]
    [InlineData("status", "/admin/status", 200, """{"Engines":[],"Threads":[],"QueueDepths":{"example":2},"FeedSubscriberFaults":0}""", "example  2 queued")]
    [InlineData("tickets", "/admin/tickets/sample", 200, """{"Id":"sample","State":"Queued"}""", "State: Queued")]
    [InlineData("switch", "/admin/switch", 202, """{"OperationId":"op","Engine":"cpu","Model":"example"}""", "OperationId: op")]
    [InlineData("events", "/admin/events", 200, """{"Timestamp":"now","Seq":1,"Kind":"ticket.done","TicketId":"sample"}""", "now  ticket.done  Seq=1")]
    [InlineData("tickets", "/admin/tickets/sample", 404, """{"error":{"code":"not_found","message":"no such ticket"}}""", "HTTP 404: not_found  no such ticket")]
    public async Task OperatorAndMachineModes(string verb, string path, int status, string json, string expected)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.Run(async context =>
        {
            context.Response.StatusCode = context.Request.Path == path ? status : 400;
            var body = verb == "events" ? $"data: {json}\n\n" : json;
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(body), deadline.Token);
        });
        await app.StartAsync(deadline.Token);
        try
        {
            string[] options = verb switch
            {
                "tickets" => ["--server", app.Urls.Single(), "sample"],
                "switch" => ["--server", app.Urls.Single(), "-e", "cpu", "-m", "example"],
                _ => ["--server", app.Urls.Single()],
            };
            var human = await CliCompletionProcessTests.RunCliAsync(verb, options, null, deadline.Token);
            Assert.Equal(status >= 400 ? 1 : 0, human.ExitCode);
            Assert.Contains(expected, Encoding.UTF8.GetString(human.Stdout), StringComparison.Ordinal);

            var machine = await CliCompletionProcessTests.RunCliAsync("--json", [verb, .. options], null, deadline.Token);
            Assert.Equal(human.ExitCode, machine.ExitCode);
            Assert.Equal(json + Environment.NewLine, Encoding.UTF8.GetString(machine.Stdout));
        }
        finally
        {
            await app.StopAsync(deadline.Token);
        }
    }
}
