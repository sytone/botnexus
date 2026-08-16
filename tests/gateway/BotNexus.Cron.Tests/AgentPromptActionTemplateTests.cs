using BotNexus.Cron.Actions;
using BotNexus.Cron.Prompts;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Triggers;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BotNexus.Cron.Tests;

public sealed class AgentPromptActionTemplateTests
{
    [Fact]
    public async Task ExecuteAsync_WithTemplateName_RendersTemplateAndUsesRenderedPrompt()
    {
        var trigger = new Mock<IInternalTrigger>();
        var resolver = new Mock<IPromptTemplateResolver>();
        string? capturedPrompt = null;
        trigger.SetupGet(value => value.Type).Returns(TriggerType.Cron);
        trigger.Setup(value => value.CreateSessionAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<InternalTriggerRequest?>()))
            .Callback<AgentId, string, CancellationToken, InternalTriggerRequest?>((_, prompt, _, _) => capturedPrompt = prompt)
            .ReturnsAsync(SessionId.From("cron:template:run-1"));
        resolver.Setup(value => value.TryRender(AgentId.From("agent-a"), "daily-status", It.IsAny<IReadOnlyDictionary<string, string?>?>(), out It.Ref<string>.IsAny, out It.Ref<string?>.IsAny))
            .Returns((AgentId _, string __, IReadOnlyDictionary<string, string?>? ___, out string rendered, out string? error) =>
            {
                rendered = "Rendered prompt";
                error = null;
                return true;
            });

        var action = new AgentPromptAction();
        var context = CreateContext(
            BuildServices(trigger.Object, resolver.Object),
            message: "Inline fallback",
            templateName: "daily-status");

        await action.ExecuteAsync(context);

        capturedPrompt.ShouldBe("Rendered prompt");
    }

    [Fact]
    public async Task ExecuteAsync_WithoutTemplate_PreservesInlinePromptVerbatim()
    {
        var trigger = new Mock<IInternalTrigger>();
        string? capturedPrompt = null;
        trigger.SetupGet(value => value.Type).Returns(TriggerType.Cron);
        trigger.Setup(value => value.CreateSessionAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<InternalTriggerRequest?>()))
            .Callback<AgentId, string, CancellationToken, InternalTriggerRequest?>((_, prompt, _, _) => capturedPrompt = prompt)
            .ReturnsAsync(SessionId.From("cron:inline:run-1"));

        var action = new AgentPromptAction();
        var context = CreateContext(
            BuildServices(trigger.Object, resolver: null),
            message: "Keep inline {{prompt}} text",
            templateName: null);

        await action.ExecuteAsync(context);

        capturedPrompt.ShouldBe("Keep inline {{prompt}} text");
    }

    // #3210: the action now preflights the job's agent, so these fixtures must present a REGISTERED
    // agent. A bare Mock.Of<IAgentRegistry>() returns null for Get(...) which is now (correctly) a
    // classified "agent is not registered" failure rather than a silent fall-through.
    private static IAgentRegistry BuildRegistryWithRegisteredAgent()
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(value => value.Get(It.IsAny<AgentId>())).Returns(new AgentDescriptor
        {
            AgentId = AgentId.From("agent-a"),
            DisplayName = "Agent A",
            ModelId = "gpt-4.1",
            ApiProvider = "copilot"
        });
        return registry.Object;
    }

    private static IServiceProvider BuildServices(IInternalTrigger trigger, IPromptTemplateResolver? resolver)
    {
        var services = new ServiceCollection()
            .AddSingleton<IInternalTrigger>(trigger)
            .AddSingleton(BuildRegistryWithRegisteredAgent());

        if (resolver is not null)
            services.AddSingleton(resolver);

        return services.BuildServiceProvider();
    }

    private static CronExecutionContext CreateContext(IServiceProvider services, string? message, string? templateName)
        => new()
        {
            Job = new CronJob
            {
                Id = JobId.From("job-1"),
                Name = "Cron prompt",
                Schedule = "*/1 * * * *",
                ActionType = "agent-prompt",
                AgentId = AgentId.From("agent-a"),
                Message = message,
                TemplateName = templateName,
                TemplateParameters = new Dictionary<string, string?> { ["owner"] = "Hermes" },
                CreatedAt = DateTimeOffset.UtcNow,
                Enabled = true
            },
            RunId = RunId.From("run-1"),
            TriggeredAt = DateTimeOffset.UtcNow,
            TriggerType = CronTriggerType.Scheduled,
            Services = services
        };
}
