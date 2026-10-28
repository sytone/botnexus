using System.Text.Json;
using BotNexus.Cron;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Api.Models;
using Xunit;
using Shouldly;

namespace BotNexus.Gateway.Tests;

public sealed class CronPreconditionContractTests
{
    [Fact]
    public void Requests_CreatePreserveClearSwitchAndRoundTripProbe()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var create = JsonSerializer.Deserialize<CronJobCreateRequest>("""{"name":"Probe","schedule":"* * * * *","actionType":"agent-prompt","preconditionCommand":"exit 10"}""", options).ShouldNotBeNull();
        var job = create.ToCronJob();
        job.PreconditionCommand.ShouldBe("exit 10");
        var retained = JsonSerializer.Deserialize<CronJobUpdateRequest>("""{"name":"Rename"}""", options).ShouldNotBeNull().ApplyTo(job);
        retained.PreconditionCommand.ShouldBe("exit 10");
        CronJobUpdateRequest.FromCronJob(job).ApplyTo(job).PreconditionCommand.ShouldBe("exit 10");
        foreach (var json in new[] { """{"preconditionCommand":null}""", """{"preconditionCommand":""}""", """{"actionType":"command","shellCommand":"exit 0"}""" })
            JsonSerializer.Deserialize<CronJobUpdateRequest>(json, options).ShouldNotBeNull().ApplyTo(job).PreconditionCommand.ShouldBeNull();
        JsonSerializer.Deserialize<CronJobUpdateRequest>("""{"preconditionCommand":"exit 0"}""", options).ShouldNotBeNull().ApplyTo(job).PreconditionCommand.ShouldBe("exit 0");
    }
}
