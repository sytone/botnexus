using BotNexus.Cron.Actions;
using BotNexus.Cron.Prompts;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Triggers;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BotNexus.Cron.Tests;

public sealed class AgentPromptActionTests
{
    [Fact]
    public async Task ExecuteAsync_CreatesSessionUsingCronTrigger()
    {
        var action = new AgentPromptAction();
        var trigger = new Mock<IInternalTrigger>();
        var registry = new Mock<IAgentRegistry>();
        AgentId? capturedAgentId = null;
        string? capturedPrompt = null;
        InternalTriggerRequest? capturedRequest = null;
        var createdSession = SessionId.From("cron:job-1:run-1");

        trigger.SetupGet(value => value.Type).Returns(TriggerType.Cron);
        trigger.Setup(value => value.CreateSessionAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<InternalTriggerRequest?>()))
            .Callback<AgentId, string, CancellationToken, InternalTriggerRequest?>((agentId, prompt, _, request) =>
            {
                capturedAgentId = agentId;
                capturedPrompt = prompt;
                capturedRequest = request;
            })
            .ReturnsAsync(createdSession);

        registry.Setup(value => value.Get(AgentId.From("agent-a"))).Returns((AgentDescriptor?)null);
        var services = BuildServices(trigger.Object, registry.Object);
        var context = CreateContext(services, model: "openai/gpt-4.1");

        await action.ExecuteAsync(context);

        capturedAgentId.ShouldBe(AgentId.From("agent-a"));
        capturedPrompt.ShouldBe("Ping from cron");
        capturedRequest.ShouldNotBeNull();
        capturedRequest!.CronJobId!.Value.Value.ShouldBe("job-1");
        capturedRequest.ModelOverride.ShouldBe("openai/gpt-4.1");
        context.SessionId!.Value.ShouldBe(createdSession);
        trigger.Verify(value => value.CreateSessionAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<InternalTriggerRequest?>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsWhenCronTriggerMissing()
    {
        var action = new AgentPromptAction();
        var services = new ServiceCollection().BuildServiceProvider();
        var context = CreateContext(services);

        var act = () => action.ExecuteAsync(context);
        var ex = await act.ShouldThrowAsync<InvalidOperationException>();
        ex.Message.ShouldContain("Cron internal trigger is not registered");
    }

    [Fact]
    public async Task ExecuteAsync_SoulAgent_UsesSoulTrigger()
    {
        var action = new AgentPromptAction();
        var cronTrigger = new Mock<IInternalTrigger>();
        var soulTrigger = new Mock<IInternalTrigger>();
        var registry = new Mock<IAgentRegistry>();
        var descriptor = new AgentDescriptor
        {
            AgentId = AgentId.From("agent-a"),
            DisplayName = "Agent A",
            ModelId = "gpt-4.1",
            ApiProvider = "copilot",
            Soul = new SoulAgentConfig { Enabled = true }
        };

        registry.Setup(value => value.Get(AgentId.From("agent-a"))).Returns(descriptor);
        cronTrigger.SetupGet(value => value.Type).Returns(TriggerType.Cron);
        soulTrigger.SetupGet(value => value.Type).Returns(TriggerType.Soul);
        soulTrigger.Setup(value => value.CreateSessionAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<InternalTriggerRequest?>()))
            .ReturnsAsync(SessionId.From("soul:agent-a:2026-05-08"));

        var services = BuildServices(cronTrigger.Object, soulTrigger.Object, registry.Object);
        var context = CreateContext(services);

        await action.ExecuteAsync(context);

        soulTrigger.Verify(value =>
            value.CreateSessionAsync(AgentId.From("agent-a"), "Ping from cron", It.IsAny<CancellationToken>(), It.IsAny<InternalTriggerRequest?>()), Times.Once);
        cronTrigger.Verify(value =>
            value.CreateSessionAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<InternalTriggerRequest?>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ForwardsTriggerReportedToolInvocationCount_ToTheExecutionContext()
    {
        // #2985: the scheduler's zero-tool rule can only fire if the count actually crosses the
        // action seam. Without this forwarding the marker would be accepted, persisted, documented
        // -- and completely inert, which is the most expensive way to ship a detection fix.
        var action = new AgentPromptAction();
        var trigger = new Mock<IInternalTrigger>();
        var registry = new Mock<IAgentRegistry>();

        trigger.SetupGet(value => value.Type).Returns(TriggerType.Cron);
        trigger.Setup(value => value.CreateSessionAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<InternalTriggerRequest?>()))
            .Callback<AgentId, string, CancellationToken, InternalTriggerRequest?>((_, _, _, request) =>
            {
                // Stand in for the trigger writing back its turn's tool count.
                if (request is not null)
                    request.ToolInvocationCount = 0;
            })
            .ReturnsAsync(SessionId.From("cron:job-1:run-1"));
        registry.Setup(value => value.Get(AgentId.From("agent-a"))).Returns((AgentDescriptor?)null);

        var context = CreateContext(BuildServices(trigger.Object, registry.Object));

        await action.ExecuteAsync(context);

        context.ToolInvocationCount.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTriggerReportsNoToolCount_LeavesContextCountNull()
    {
        // #2985: null must stay null across the seam. If the action defaulted an unreported count
        // to zero, every command/webhook-shaped run would look like a do-nothing run.
        var action = new AgentPromptAction();
        var trigger = new Mock<IInternalTrigger>();
        var registry = new Mock<IAgentRegistry>();

        trigger.SetupGet(value => value.Type).Returns(TriggerType.Cron);
        trigger.Setup(value => value.CreateSessionAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<InternalTriggerRequest?>()))
            .ReturnsAsync(SessionId.From("cron:job-1:run-1"));
        registry.Setup(value => value.Get(AgentId.From("agent-a"))).Returns((AgentDescriptor?)null);

        var context = CreateContext(BuildServices(trigger.Object, registry.Object));

        await action.ExecuteAsync(context);

        context.ToolInvocationCount.ShouldBeNull();
    }

    private static IServiceProvider BuildServices(IInternalTrigger trigger, IAgentRegistry? registry = null)
        => new ServiceCollection()
            .AddSingleton<IInternalTrigger>(trigger)
            .AddSingleton(registry ?? Mock.Of<IAgentRegistry>())
            .BuildServiceProvider();

    private static IServiceProvider BuildServices(IInternalTrigger trigger1, IInternalTrigger trigger2, IAgentRegistry registry)
        => new ServiceCollection()
            .AddSingleton<IInternalTrigger>(trigger1)
            .AddSingleton<IInternalTrigger>(trigger2)
            .AddSingleton(registry)
            .BuildServiceProvider();

    private static CronExecutionContext CreateContext(IServiceProvider services, string? model = null)
        => new()
        {
            Job = new CronJob
            {
                Id = JobId.From("job-1"),
                Name = "Cron prompt",
                Schedule = "*/1 * * * *",
                ActionType = "agent-prompt",
                AgentId = AgentId.From("agent-a"),
                Message = "Ping from cron",
                Model = model,
                CreatedBy = "tester",
                CreatedAt = DateTimeOffset.UtcNow,
                Enabled = true
            },
            RunId = RunId.From("run-1"),
            TriggeredAt = DateTimeOffset.UtcNow,
            TriggerType = CronTriggerType.Scheduled,
            Services = services
        };

    [Fact]
    public async Task ExecuteAsync_PropagatesConversationId_WhenJobHasConversationId()
    {
        // Verify that CronJob.ConversationId flows through to InternalTriggerRequest.ConversationId
        var action = new AgentPromptAction();
        var trigger = new Mock<IInternalTrigger>();
        var registry = new Mock<IAgentRegistry>();
        InternalTriggerRequest? capturedRequest = null;

        trigger.SetupGet(value => value.Type).Returns(TriggerType.Cron);
        trigger.Setup(value => value.CreateSessionAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<InternalTriggerRequest?>()))
            .Callback<AgentId, string, CancellationToken, InternalTriggerRequest?>((_, _, _, request) => capturedRequest = request)
            .ReturnsAsync(SessionId.From("cron:job-pinned:run-1"));

        registry.Setup(value => value.Get(AgentId.From("agent-a"))).Returns((AgentDescriptor?)null);
        var services = BuildServices(trigger.Object, registry.Object);

        var context = new CronExecutionContext
        {
            Job = new CronJob
            {
                Id = JobId.From("job-pinned"),
                Name = "Pinned conversation job",
                Schedule = "*/1 * * * *",
                ActionType = "agent-prompt",
                AgentId = AgentId.From("agent-a"),
                Message = "Run in pinned conversation",
                ConversationId = ConversationId.From("conv-explicit-123"),
                CreatedAt = DateTimeOffset.UtcNow,
                Enabled = true
            },
            RunId = RunId.From("run-1"),
            TriggeredAt = DateTimeOffset.UtcNow,
            TriggerType = CronTriggerType.Scheduled,
            Services = services
        };

        await action.ExecuteAsync(context);

        capturedRequest.ShouldNotBeNull();
        capturedRequest!.ConversationId!.Value.Value.ShouldBe("conv-explicit-123");
        capturedRequest.CronJobId!.Value.Value.ShouldBe("job-pinned");
    }

    [Fact]
    public async Task ExecuteAsync_UsesPromptTemplateResolver_WhenTemplateNameProvided()
    {
        var action = new AgentPromptAction();
        var trigger = new Mock<IInternalTrigger>();
        var resolver = new Mock<IPromptTemplateResolver>();
        var registry = new Mock<IAgentRegistry>();
        string? capturedPrompt = null;

        trigger.SetupGet(value => value.Type).Returns(TriggerType.Cron);
        trigger.Setup(value => value.CreateSessionAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<InternalTriggerRequest?>()))
            .Callback<AgentId, string, CancellationToken, InternalTriggerRequest?>((_, prompt, _, _) => capturedPrompt = prompt)
            .ReturnsAsync(SessionId.From("cron:templated:run-1"));
        resolver.Setup(value => value.TryRender(AgentId.From("agent-a"), "daily-summary", It.IsAny<IReadOnlyDictionary<string, string?>?>(), out It.Ref<string>.IsAny, out It.Ref<string?>.IsAny))
            .Returns((AgentId _, string __, IReadOnlyDictionary<string, string?>? ___, out string renderedPrompt, out string? error) =>
            {
                renderedPrompt = "Rendered prompt";
                error = null;
                return true;
            });

        registry.Setup(value => value.Get(AgentId.From("agent-a"))).Returns((AgentDescriptor?)null);
        var services = new ServiceCollection()
            .AddSingleton<IInternalTrigger>(trigger.Object)
            .AddSingleton(resolver.Object)
            .AddSingleton(registry.Object)
            .BuildServiceProvider();
        var context = CreateContext(services) with
        {
            Job = CreateContext(services).Job with
            {
                Message = null,
                TemplateName = "daily-summary",
                TemplateParameters = new Dictionary<string, string?> { ["owner"] = "Hermes" }
            }
        };

        await action.ExecuteAsync(context);

        capturedPrompt.ShouldBe("Rendered prompt");
    }

}
