using BotNexus.Cron;
using BotNexus.Cron.Actions;
using BotNexus.Cron.Tools;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Abstractions.Triggers;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed partial class CronControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authoring_UnpinnedApiOrTool_DispatchesGlobalAfterAgentDefaultChange(bool viaTool)
    {
        var store = new FakeCronStore();
        var options = new CronOptions { DefaultModel = "openai/gpt-4.1-mini" };
        var descriptor = new AgentDescriptor { AgentId = AgentId.From("owner"), DisplayName = "Owner", ApiProvider = "openai", ModelId = "gpt-4.1" };
        var agents = new Mock<IAgentRegistry>();
        agents.Setup(a => a.Get(descriptor.AgentId)).Returns(() => descriptor);
        var models = new BotNexus.Agent.Providers.Core.Registry.ModelRegistry();
        foreach (var id in new[] { "gpt-4.1", "gpt-4.1-mini" })
            models.Register("openai", new BotNexus.Agent.Providers.Core.Models.LlmModel(Id: id, Name: id, Provider: "openai", Api: "openai-completions", BaseUrl: "https://example.invalid", Reasoning: false, Input: ["text"], Cost: new(0, 0, 0, 0), ContextWindow: 1000, MaxTokens: 100));
        var requests = new List<InternalTriggerRequest>();
        var trigger = new Mock<IInternalTrigger>();
        trigger.SetupGet(t => t.Type).Returns(TriggerType.Cron);
        trigger.Setup(t => t.CreateSessionAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<InternalTriggerRequest>()))
            .Callback<AgentId, string, CancellationToken, InternalTriggerRequest>((_, _, _, request) => requests.Add(request))
            .ReturnsAsync(SessionId.From("authored"));
        var redactor = new Mock<ISecretRedactor>();
        redactor.Setup(r => r.Redact(It.IsAny<string>())).Returns<string>(s => s);
        redactor.Setup(r => r.RedactForExternalDelivery(It.IsAny<string>())).Returns<string>(s => s);
        using var services = new ServiceCollection().AddSingleton(agents.Object).AddSingleton(trigger.Object).AddSingleton(models).AddSingleton(redactor.Object).BuildServiceProvider();
        var monitor = new StaticOptionsMonitor<CronOptions>(options);
        var scheduler = new CronScheduler(store, [new AgentPromptAction()], services.GetRequiredService<IServiceScopeFactory>(), monitor, NullLogger<CronScheduler>.Instance);
        var controller = new CronController(store, scheduler, monitor, NullLogger<CronController>.Instance, modelRegistry: models);
        var tool = new CronTool(store, scheduler, descriptor.AgentId, modelRegistry: models, cronOptions: options);
        if (viaTool)
        {
            var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["action"] = "create", ["name"] = "Authored", ["schedule"] = "* * * * *", ["message"] = "test" });
            await tool.ExecuteAsync("create", args);
        }
        else
        {
            var created = await controller.Create(new CronJobCreateRequest { Name = "Authored", Schedule = "* * * * *", ActionType = "agent-prompt", AgentId = "owner", Message = "test" }, default);
            created.Result.ShouldBeOfType<CreatedAtActionResult>();
        }
        var job = (await store.ListAsync()).ShouldHaveSingleItem();
        job.Model.ShouldBeNull();
        descriptor = descriptor with { ModelId = "gpt-4.1-mini" };
        (await scheduler.RunNowAsync(job.Id)).Status.ShouldBe(CronRunStatus.Ok);
        descriptor = descriptor with { ModelId = "gpt-4.1" };
        (await scheduler.RunNowAsync(job.Id)).Status.ShouldBe(CronRunStatus.Ok);
        requests.Count.ShouldBe(2);
        requests.ShouldAllBe(r => r.ModelOverride == "openai/gpt-4.1-mini" && r.CronModelSource == "global");
        var list = await controller.List(default);
        var projected = list.Result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeAssignableTo<IReadOnlyList<CronJob>>().ShouldHaveSingleItem();
        projected.ConfiguredDefaultModel.ShouldBe(options.DefaultModel);
        projected.ModelRoutingIndicator.ShouldBe("global-default");
        (await store.GetAsync(job.Id)).ShouldNotBeNull().Model.ShouldBeNull();
    }
}
