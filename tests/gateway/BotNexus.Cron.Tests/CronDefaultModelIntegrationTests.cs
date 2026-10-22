using BotNexus.Cron.Actions;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Domain.AgentExchange;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Abstractions.Triggers;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.IO.Abstractions;

namespace BotNexus.Cron.Tests;

public sealed class CronDefaultModelIntegrationTests
{
    [Theory]
    [InlineData("agent-prompt")]
    [InlineData("heartbeat")]
    [InlineData("memory-dreaming")]
    [InlineData("skill-review")]
    [InlineData("agent-converse")]
    public async Task Dispatch_ActualAction_PassesOccurrenceModel(string actionType)
    {
        using var fixture = new Fixture(actionType);
        var run = await fixture.Scheduler.RunNowAsync(fixture.Job.Id);
        run.Status.ShouldBe(CronRunStatus.Ok);
        fixture.RequestedModels.ShouldHaveSingleItem().ShouldBe("openai/gpt-4.1-mini");
        (await fixture.Store.GetRunAsync(run.Id)).ShouldNotBeNull().EffectiveModel.ShouldNotBeNull().Source.ShouldBe("global");
        (await fixture.Store.GetAsync(fixture.Job.Id)).ShouldNotBeNull().Model.ShouldBeNull();
    }

    [Fact]
    public async Task Dispatch_UnsetMinimalRegistry_PreservesLegacy()
    {
        using var fixture = new Fixture("agent-prompt", defaultModel: null, registerModels: false);
        var run = await fixture.Scheduler.RunNowAsync(fixture.Job.Id);
        run.Status.ShouldBe(CronRunStatus.Ok);
        fixture.RequestedModels.ShouldHaveSingleItem().ShouldBeNull();
        (await fixture.Store.GetRunAsync(run.Id)).ShouldNotBeNull().EffectiveModel.ShouldBeNull();
    }

    [Fact]
    public async Task Dispatch_UnsetRemoteExchange_PreservesLegacy()
    {
        using var fixture = new Fixture("agent-converse", defaultModel: null, target: "other:remote");
        var run = await fixture.Scheduler.RunNowAsync(fixture.Job.Id);
        run.Status.ShouldBe(CronRunStatus.Ok);
        fixture.RequestedModels.ShouldHaveSingleItem().ShouldBeNull();
    }

    [Theory]
    [InlineData("openai/unknown", false)]
    [InlineData("openai/gpt-4.1-mini", true)]
    public async Task Dispatch_InvalidOrForbiddenGlobal_RejectsBeforeAction(string model, bool forbidden)
    {
        using var fixture = new Fixture("agent-prompt", defaultModel: model, forbidden: forbidden);
        var run = await fixture.Scheduler.RunNowAsync(fixture.Job.Id);
        run.Status.ShouldNotBe(CronRunStatus.Ok);
        fixture.RequestedModels.ShouldBeEmpty();
    }

    [Fact]
    public async Task Dispatch_UnpinnedSystemJob_GlobalBeatsChangedAgentDefault()
    {
        using var fixture = new Fixture("heartbeat");
        fixture.Descriptor = fixture.Descriptor with { ModelId = "gpt-4.1-mini" };
        var first = await fixture.Scheduler.RunNowAsync(fixture.Job.Id);
        fixture.Descriptor = fixture.Descriptor with { ModelId = "gpt-4.1" };
        var second = await fixture.Scheduler.RunNowAsync(fixture.Job.Id);
        first.Status.ShouldBe(CronRunStatus.Ok);
        second.Status.ShouldBe(CronRunStatus.Ok);
        fixture.RequestedModels.ShouldBe(new string?[] { "openai/gpt-4.1-mini", "openai/gpt-4.1-mini" });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispatch_UnsetHeartbeat_LeavesTrueConversationInheritanceToRuntime(bool jobConversationHasOverride)
    {
        using var fixture = new Fixture("heartbeat", defaultModel: null);
        fixture.Conversation.ModelOverride = jobConversationHasOverride ? "openai/gpt-4.1-mini" : null;
        var run = await fixture.Scheduler.RunNowAsync(fixture.Job.Id);
        run.Status.ShouldBe(CronRunStatus.Ok);
        fixture.RequestedModels.ShouldHaveSingleItem().ShouldBeNull();
        (await fixture.Store.GetRunAsync(run.Id)).ShouldNotBeNull().EffectiveModel.ShouldBeNull();
    }

    [Fact]
    public async Task Dispatch_Heartbeat_ChangedAllowlistRejectsNextOccurrence()
    {
        using var fixture = new Fixture("heartbeat");
        (await fixture.Scheduler.RunNowAsync(fixture.Job.Id)).Status.ShouldBe(CronRunStatus.Ok);
        fixture.Descriptor = fixture.Descriptor with { AllowedModelIds = ["gpt-4.1"] };
        (await fixture.Scheduler.RunNowAsync(fixture.Job.Id)).Status.ShouldNotBe(CronRunStatus.Ok);
        fixture.RequestedModels.ShouldHaveSingleItem().ShouldBe("openai/gpt-4.1-mini");
    }

    [Fact]
    public void Projection_ShowsConfiguredDefaultAndJobOverride()
    {
        var method = typeof(CronModelRouting).GetMethod("Project");
        method.ShouldNotBeNull();
        var job = new CronJob { Id = JobId.From("projection"), Name = "Projection", Schedule = "* * * * *", ActionType = "heartbeat" };
        var projected = method.Invoke(null, [job, "openai/gpt-4.1-mini"]).ShouldBeOfType<CronJob>();
        projected.GetType().GetProperty("ConfiguredDefaultModel").ShouldNotBeNull().GetValue(projected).ShouldBe("openai/gpt-4.1-mini");
        projected.ModelRoutingIndicator.ShouldBe("global-default");
        var pinned = method.Invoke(null, [job with { Model = "openai/gpt-4.1" }, "openai/gpt-4.1-mini"]).ShouldBeOfType<CronJob>();
        pinned.ModelRoutingIndicator.ShouldBe("job-override");
    }

    [Theory]
    [InlineData(null, "openai/gpt-4.1-mini", "openai/gpt-4.1-mini", "global")]
    [InlineData(null, null, "openai/gpt-4.1", "conversation")]
    [InlineData("openai/gpt-4.1", "openai/gpt-4.1-mini", "openai/gpt-4.1", "job")]
    public async Task Dispatch_ConversationPrecedence_DoesNotMutateStandingModel(string? pin, string? global, string expected, string source)
    {
        using var fixture = new Fixture("agent-prompt", defaultModel: global);
        fixture.Conversation.ModelOverride = "openai/gpt-4.1";
        await fixture.Store.UpdateDefinitionAsync(fixture.Job with { Model = pin, ConversationId = fixture.Conversation.ConversationId });
        // Conversation pinning has a separate CAS authority; fixture job is created with it below.
        var run = await fixture.Scheduler.RunNowAsync(fixture.Job.Id);
        run.Status.ShouldBe(CronRunStatus.Ok);
        fixture.RequestedModels.ShouldHaveSingleItem().ShouldBe(expected);
        (await fixture.Store.GetRunAsync(run.Id)).ShouldNotBeNull().EffectiveModel.ShouldNotBeNull().Source.ShouldBe(source);
        fixture.Conversation.ModelOverride.ShouldBe("openai/gpt-4.1");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Tool_PrepareAndUpdate_ExplicitClearRestoresGlobal(string? clear)
    {
        using var fixture = new Fixture("agent-prompt");
        await fixture.Store.UpdateDefinitionAsync(fixture.Job with { Model = "openai/gpt-4.1" });
        var prepared = await fixture.Tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["action"] = "update", ["jobId"] = fixture.Job.Id.Value, ["model"] = clear });
        prepared.ShouldContainKey("model");
        await fixture.Tool.ExecuteAsync("clear", prepared);
        (await fixture.Store.GetAsync(fixture.Job.Id)).ShouldNotBeNull().Model.ShouldBeNullOrWhiteSpace();
        var run = await fixture.Scheduler.RunNowAsync(fixture.Job.Id);
        run.Status.ShouldBe(CronRunStatus.Ok);
        fixture.RequestedModels.ShouldHaveSingleItem().ShouldBe("openai/gpt-4.1-mini");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispatch_PinnedExchange_RejectsEitherParticipantPermission(bool forbidTarget)
    {
        using var fixture = new Fixture("agent-converse", forbidden: !forbidTarget);
        if (forbidTarget) fixture.TargetDescriptor = fixture.TargetDescriptor with { AllowedModelIds = ["gpt-4.1"] };
        var run = await fixture.Scheduler.RunNowAsync(fixture.Job.Id);
        run.Status.ShouldNotBe(CronRunStatus.Ok);
        fixture.RequestedModels.ShouldBeEmpty();
    }

    [Fact]
    public async Task Dispatch_UnsetForbiddenStandingModel_PreservesAgentFallback()
    {
        using var fixture = new Fixture("agent-prompt", defaultModel: null, forbidden: true);
        fixture.Conversation.ModelOverride = "openai/gpt-4.1-mini";
        var run = await fixture.Scheduler.RunNowAsync(fixture.Job.Id);
        run.Status.ShouldBe(CronRunStatus.Ok);
        fixture.RequestedModels.ShouldHaveSingleItem().ShouldBe("openai/gpt-4.1");
        fixture.Conversation.ModelOverride.ShouldBe("openai/gpt-4.1-mini");
    }

    [Fact]
    public void SessionMetadata_UnsetOccurrence_RemovesPriorCronPin()
    {
        var session = new GatewaySession { SessionId = SessionId.From("reused"), AgentId = AgentId.From("owner") };
        new InternalTriggerRequest { ModelOverride = "openai/gpt-4.1-mini", CronRequestedModel = "gpt-4.1-mini", CronModelSource = "global" }.ApplyCronModelMetadata(session);
        session.Metadata["cronEffectiveProvider"].ShouldBe("openai");
        session.Metadata["cronEffectiveModel"].ShouldBe("gpt-4.1-mini");
        new InternalTriggerRequest().ApplyCronModelMetadata(session);
        session.Metadata.ShouldNotContainKey("cronModelOverride");
        session.Metadata.ShouldNotContainKey("cronModelSource");
        session.Metadata.ShouldNotContainKey("cronEffectiveModel");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root;
        private readonly ServiceProvider services;
        public AgentDescriptor Descriptor { get; set; } = new() { AgentId = AgentId.From("owner"), DisplayName = "Owner", ApiProvider = "openai", ModelId = "gpt-4.1" };
        public AgentDescriptor TargetDescriptor { get; set; } = new() { AgentId = AgentId.From("peer"), DisplayName = "Peer", ApiProvider = "openai", ModelId = "gpt-4.1" };
        public Conversation Conversation { get; } = new() { ConversationId = ConversationId.From("standing"), AgentId = AgentId.From("owner") };
        public Tools.CronTool Tool { get; }
        public List<string?> RequestedModels { get; } = [];
        public SqliteCronStore Store { get; }
        public CronScheduler Scheduler { get; }
        public CronJob Job { get; }

        public Fixture(string actionType, string? defaultModel = "openai/gpt-4.1-mini", bool registerModels = true, string target = "peer", bool forbidden = false)
        {
            root = Path.Combine(Environment.GetEnvironmentVariable("BOTNEXUS_4604_TEST_ROOT") ?? Path.GetTempPath(), "cron-integration", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var memory = Path.Combine(root, "agents", "owner", "workspace", "memory");
            Directory.CreateDirectory(memory);
            File.WriteAllText(Path.Combine(memory, DateTime.UtcNow.ToString("yyyy-MM-dd") + ".md"), "Remember this test.");
            Store = new SqliteCronStore(Path.Combine(root, "cron.db"), new FileSystem());
            Job = new CronJob { Id = JobId.From("integration"), Name = "Integration", Schedule = "* * * * *", ActionType = actionType, AgentId = Descriptor.AgentId, Message = "test", System = actionType == "heartbeat", ConversationId = Conversation.ConversationId, Metadata = new Dictionary<string, object?> { ["targetAgentId"] = target, ["minToolCalls"] = 1 } };
            if (forbidden) Descriptor = Descriptor with { AllowedModelIds = ["gpt-4.1"] };
            var agents = new Mock<IAgentRegistry>();
            agents.Setup(a => a.Get(AgentId.From("owner"))).Returns(() => Descriptor);
            agents.Setup(a => a.Get(AgentId.From("peer"))).Returns(() => TargetDescriptor);
            var trigger = new Mock<IInternalTrigger>();
            trigger.SetupGet(t => t.Type).Returns(TriggerType.Cron);
            trigger.Setup(t => t.CreateSessionAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<InternalTriggerRequest>()))
                .Callback<AgentId, string, CancellationToken, InternalTriggerRequest>((_, _, _, request) => { RequestedModels.Add(request.ModelOverride); request.CronModelSource.ShouldBeOneOf(null, "global", "job", "conversation", "agent"); })
                .ReturnsAsync(SessionId.From("occurrence"));
            var exchange = new Mock<IAgentExchangeService>();
            exchange.Setup(e => e.ConverseAsync(It.IsAny<AgentExchangeRequest>(), It.IsAny<CancellationToken>()))
                .Callback<AgentExchangeRequest, CancellationToken>((request, _) => RequestedModels.Add(request.ModelOverride))
                .ReturnsAsync(new AgentExchangeResult { SessionId = SessionId.From("exchange"), ConversationId = ConversationId.From("exchange"), Status = "ok", Turns = 1, FinalResponse = "ok" });
            var session = new GatewaySession { SessionId = SessionId.From("history"), AgentId = Descriptor.AgentId };
            session.AddEntries([new SessionEntry { Role = MessageRole.Tool, Content = "load", ToolName = "skills", ToolCallId = "call", ToolArgs = "{}", Timestamp = DateTimeOffset.UtcNow }]);
            var sessions = new Mock<ISessionStore>();
            sessions.Setup(s => s.ListSummariesAsync(It.IsAny<DateTimeOffset>(), null, 0, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { SessionSummary.FromSession(session) });
            sessions.Setup(s => s.GetAsync(session.SessionId, It.IsAny<CancellationToken>())).ReturnsAsync(session);
            var collection = new ServiceCollection();
            collection.AddSingleton(agents.Object).AddSingleton(trigger.Object).AddSingleton(exchange.Object).AddSingleton(sessions.Object);
            var conversations = new Mock<IConversationStore>();
            conversations.Setup(c => c.GetAsync(Conversation.ConversationId, It.IsAny<CancellationToken>())).ReturnsAsync(Conversation);
            collection.AddSingleton(conversations.Object);
            collection.AddSingleton(new BotNexusHome(root));
            collection.AddSingleton<ISecretRedactor>(new Redactor());
            if (registerModels) collection.AddSingleton(CronModelPreflightTests.BuildRegistry());
            services = collection.BuildServiceProvider();
            Scheduler = new CronScheduler(Store, [new AgentPromptAction(), new HeartbeatAction(), new MemoryDreamingCronAction(), new SkillReviewCronAction(), new AgentConverseCronAction()], services.GetRequiredService<IServiceScopeFactory>(), new Monitor(new CronOptions { DefaultModel = defaultModel }), NullLogger<CronScheduler>.Instance);
            Tool = new Tools.CronTool(Store, Scheduler, Descriptor.AgentId, modelRegistry: CronModelPreflightTests.BuildRegistry(), cronOptions: new CronOptions { DefaultModel = defaultModel });
            Store.CreateAsync(Job).GetAwaiter().GetResult();
        }
        public void Dispose()
        {
            services.Dispose();
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(root, "cron.db")};Mode=ReadWriteCreate");
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
            Directory.Delete(root, true);
        }
    }
    private sealed class Monitor(CronOptions options) : IOptionsMonitor<CronOptions>
    {
        public CronOptions CurrentValue => options;
        public CronOptions Get(string? name) => options;
        public IDisposable? OnChange(Action<CronOptions, string?> listener) => null;
    }
    private sealed class Redactor : ISecretRedactor
    {
        public string Redact(string value) => value;
        public string RedactForExternalDelivery(string value) => value;
    }
}
