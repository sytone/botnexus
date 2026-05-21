using BotNexus.Cron.Actions;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Triggers;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BotNexus.Cron.Tests;

public sealed class HeartbeatActionTests
{
    [Fact]
    public async Task ExecuteAsync_ThrowsWhenAgentIdMissing()
    {
        var action = new HeartbeatAction();
        var services = new ServiceCollection().BuildServiceProvider();
        var context = CreateContext(services, agentId: null, message: "heartbeat");

        var act = () => action.ExecuteAsync(context);
        var ex = await act.ShouldThrowAsync<InvalidOperationException>();
        ex.Message.ShouldContain("agent id");
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsWhenMessageMissing()
    {
        var action = new HeartbeatAction();
        var registry = new Mock<IAgentRegistry>();
        var trigger = new Mock<IInternalTrigger>();
        trigger.SetupGet(v => v.Type).Returns(TriggerType.Cron);
        registry.Setup(v => v.Get(AgentId.From("agent-a"))).Returns((AgentDescriptor?)null);

        var services = new ServiceCollection()
            .AddSingleton(registry.Object)
            .AddSingleton<IInternalTrigger>(trigger.Object)
            .BuildServiceProvider();
        var context = CreateContext(services, agentId: "agent-a", message: "");

        var act = () => action.ExecuteAsync(context);
        var ex = await act.ShouldThrowAsync<InvalidOperationException>();
        ex.Message.ShouldContain("message prompt");
    }

    [Fact]
    public async Task ExecuteAsync_DuringQuietHours_SkipsExecution()
    {
        var action = new HeartbeatAction();
        var trigger = new Mock<IInternalTrigger>();
        var registry = new Mock<IAgentRegistry>();
        var descriptor = CreateDescriptorWithQuietHours(new QuietHoursConfig
        {
            Enabled = true,
            Start = "00:00",
            End = "23:59",
            Timezone = "UTC"
        });

        trigger.SetupGet(v => v.Type).Returns(TriggerType.Heartbeat);
        registry.Setup(v => v.Get(AgentId.From("agent-a"))).Returns(descriptor);

        var services = new ServiceCollection()
            .AddSingleton(registry.Object)
            .AddSingleton<IInternalTrigger>(trigger.Object)
            .BuildServiceProvider();
        var context = CreateContext(services);

        await action.ExecuteAsync(context);

        trigger.Verify(v => v.CreateSessionAsync(
            It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<InternalTriggerRequest?>()),
            Times.Never);
        context.SessionId.ShouldBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_OutsideQuietHours_ExecutesNormally()
    {
        var action = new HeartbeatAction();
        var trigger = new Mock<IInternalTrigger>();
        var registry = new Mock<IAgentRegistry>();
        var sessionId = SessionId.From("heartbeat:agent-a:20260101000000:abcd1234");
        var descriptor = CreateDescriptorWithQuietHours(new QuietHoursConfig
        {
            Enabled = true,
            Start = "00:00",
            End = "00:00", // empty range — never quiet
            Timezone = "UTC"
        });

        trigger.SetupGet(v => v.Type).Returns(TriggerType.Heartbeat);
        trigger.Setup(v => v.CreateSessionAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<InternalTriggerRequest?>()))
            .ReturnsAsync(sessionId);
        registry.Setup(v => v.Get(AgentId.From("agent-a"))).Returns(descriptor);

        var services = new ServiceCollection()
            .AddSingleton(registry.Object)
            .AddSingleton<IInternalTrigger>(trigger.Object)
            .BuildServiceProvider();
        var context = CreateContext(services);

        await action.ExecuteAsync(context);

        trigger.Verify(v => v.CreateSessionAsync(AgentId.From("agent-a"), "Heartbeat ping", It.IsAny<CancellationToken>(), It.IsAny<InternalTriggerRequest?>()), Times.Once);
        context.SessionId.ShouldBe(sessionId.Value);
    }

    [Fact]
    public async Task ExecuteAsync_PrefersHeartbeatTrigger_WhenRegistered()
    {
        var action = new HeartbeatAction();
        var heartbeatTrigger = new Mock<IInternalTrigger>();
        var cronTrigger = new Mock<IInternalTrigger>();
        var registry = new Mock<IAgentRegistry>();
        var sessionId = SessionId.From("heartbeat:agent-a:20260101000000:aaaabbbb");

        heartbeatTrigger.SetupGet(v => v.Type).Returns(TriggerType.Heartbeat);
        heartbeatTrigger.Setup(v => v.CreateSessionAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<InternalTriggerRequest?>()))
            .ReturnsAsync(sessionId);
        cronTrigger.SetupGet(v => v.Type).Returns(TriggerType.Cron);
        registry.Setup(v => v.Get(AgentId.From("agent-a"))).Returns((AgentDescriptor?)null);

        var services = new ServiceCollection()
            .AddSingleton(registry.Object)
            .AddSingleton<IInternalTrigger>(heartbeatTrigger.Object)
            .AddSingleton<IInternalTrigger>(cronTrigger.Object)
            .BuildServiceProvider();
        var context = CreateContext(services);

        await action.ExecuteAsync(context);

        heartbeatTrigger.Verify(v => v.CreateSessionAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<InternalTriggerRequest?>()), Times.Once);
        cronTrigger.Verify(v => v.CreateSessionAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<InternalTriggerRequest?>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_SoulAgent_UsesSoulTrigger_WhenHeartbeatTriggerAbsent()
    {
        var action = new HeartbeatAction();
        var soulTrigger = new Mock<IInternalTrigger>();
        var cronTrigger = new Mock<IInternalTrigger>();
        var registry = new Mock<IAgentRegistry>();
        var sessionId = SessionId.From("soul:agent-a:2026-05-18");
        var descriptor = new AgentDescriptor
        {
            AgentId = AgentId.From("agent-a"),
            DisplayName = "Agent A",
            ModelId = "test",
            ApiProvider = "test",
            Soul = new SoulAgentConfig { Enabled = true }
        };

        soulTrigger.SetupGet(v => v.Type).Returns(TriggerType.Soul);
        soulTrigger.Setup(v => v.CreateSessionAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<InternalTriggerRequest?>()))
            .ReturnsAsync(sessionId);
        cronTrigger.SetupGet(v => v.Type).Returns(TriggerType.Cron);
        registry.Setup(v => v.Get(AgentId.From("agent-a"))).Returns(descriptor);

        var services = new ServiceCollection()
            .AddSingleton(registry.Object)
            .AddSingleton<IInternalTrigger>(soulTrigger.Object)
            .AddSingleton<IInternalTrigger>(cronTrigger.Object)
            .BuildServiceProvider();
        var context = CreateContext(services);

        await action.ExecuteAsync(context);

        soulTrigger.Verify(v => v.CreateSessionAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<InternalTriggerRequest?>()), Times.Once);
        cronTrigger.Verify(v => v.CreateSessionAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<InternalTriggerRequest?>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_FallsBackToCronTrigger_WhenNoHeartbeatOrSoulTrigger()
    {
        var action = new HeartbeatAction();
        var cronTrigger = new Mock<IInternalTrigger>();
        var registry = new Mock<IAgentRegistry>();
        var sessionId = SessionId.From("cron:heartbeat-agent-a:20260101000000:zzzzzzzz");

        cronTrigger.SetupGet(v => v.Type).Returns(TriggerType.Cron);
        cronTrigger.Setup(v => v.CreateSessionAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<InternalTriggerRequest?>()))
            .ReturnsAsync(sessionId);
        registry.Setup(v => v.Get(AgentId.From("agent-a"))).Returns((AgentDescriptor?)null);

        var services = new ServiceCollection()
            .AddSingleton(registry.Object)
            .AddSingleton<IInternalTrigger>(cronTrigger.Object)
            .BuildServiceProvider();
        var context = CreateContext(services);

        await action.ExecuteAsync(context);

        cronTrigger.Verify(v => v.CreateSessionAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<InternalTriggerRequest?>()), Times.Once);
        context.SessionId.ShouldBe(sessionId.Value);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsWhenNoTriggerAvailable()
    {
        var action = new HeartbeatAction();
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(v => v.Get(It.IsAny<AgentId>())).Returns((AgentDescriptor?)null);

        var services = new ServiceCollection()
            .AddSingleton(registry.Object)
            .BuildServiceProvider();
        var context = CreateContext(services);

        var act = () => action.ExecuteAsync(context);
        var ex = await act.ShouldThrowAsync<InvalidOperationException>();
        ex.Message.ShouldContain("No suitable internal trigger");
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesCronJobId_AndModelOverride()
    {
        var action = new HeartbeatAction();
        var trigger = new Mock<IInternalTrigger>();
        var registry = new Mock<IAgentRegistry>();
        InternalTriggerRequest? captured = null;

        trigger.SetupGet(v => v.Type).Returns(TriggerType.Heartbeat);
        trigger.Setup(v => v.CreateSessionAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<InternalTriggerRequest?>()))
            .Callback<AgentId, string, CancellationToken, InternalTriggerRequest?>((_, _, _, req) => captured = req)
            .ReturnsAsync(SessionId.From("heartbeat:agent-a:20260101000000:aaaaaaaa"));
        registry.Setup(v => v.Get(AgentId.From("agent-a"))).Returns((AgentDescriptor?)null);

        var services = new ServiceCollection()
            .AddSingleton(registry.Object)
            .AddSingleton<IInternalTrigger>(trigger.Object)
            .BuildServiceProvider();
        var context = new CronExecutionContext
        {
            Job = new CronJob
            {
                Id = "heartbeat:agent-a",
                Name = "Heartbeat",
                Schedule = "*/30 * * * *",
                ActionType = "heartbeat",
                AgentId = "agent-a",
                Message = "Heartbeat ping",
                Model = "openai/gpt-4.1",
                Enabled = true,
                System = true,
                CreatedBy = "system:heartbeat",
                CreatedAt = DateTimeOffset.UtcNow
            },
            RunId = "run-1",
            TriggeredAt = DateTimeOffset.UtcNow,
            TriggerType = CronTriggerType.Scheduled,
            Services = services
        };

        await action.ExecuteAsync(context);

        captured.ShouldNotBeNull();
        captured!.CronJobId.ShouldBe("heartbeat:agent-a");
        captured.ModelOverride.ShouldBe("openai/gpt-4.1");
    }

    // -----------------------------------------------------------------
    // Phase 4: HEARTBEAT.md pre-check — IsEffectivelyEmpty
    // -----------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n\n")]
    public void IsEffectivelyEmpty_NullOrWhitespace_ReturnsTrue(string? content)
        => HeartbeatAction.IsEffectivelyEmpty(content).ShouldBeTrue();

    [Theory]
    [InlineData("# HEARTBEAT.md")]
    [InlineData("# Tasks\n## Pending")]
    [InlineData("---")]
    [InlineData("***")]
    [InlineData("---\n# Heading\n---")]
    public void IsEffectivelyEmpty_OnlyHeadingsAndRules_ReturnsTrue(string content)
        => HeartbeatAction.IsEffectivelyEmpty(content).ShouldBeTrue();

    [Theory]
    [InlineData("- [ ]")]
    [InlineData("- [ ] ")]
    [InlineData("- [ ] \n- [ ] ")]
    [InlineData("# Tasks\n- [ ]\n- [ ] ")]
    public void IsEffectivelyEmpty_OnlyEmptyCheckboxes_ReturnsTrue(string content)
        => HeartbeatAction.IsEffectivelyEmpty(content).ShouldBeTrue();

    [Fact]
    public void IsEffectivelyEmpty_HtmlCommentsOnly_ReturnsTrue()
        => HeartbeatAction.IsEffectivelyEmpty("<!-- pending tasks go here -->\n# HEARTBEAT.md").ShouldBeTrue();

    [Fact]
    public void IsEffectivelyEmpty_DefaultStubContent_ReturnsTrue()
        => HeartbeatAction.IsEffectivelyEmpty("# HEARTBEAT.md\n\n_No pending tasks._").ShouldBeFalse();

    [Theory]
    [InlineData("- [ ] Deploy the thing")]
    [InlineData("- [x] Already done")]
    [InlineData("# Tasks\n- [ ] Something pending")]
    [InlineData("Run the migration script")]
    [InlineData("_No pending tasks._")]
    public void IsEffectivelyEmpty_WithContent_ReturnsFalse(string content)
        => HeartbeatAction.IsEffectivelyEmpty(content).ShouldBeFalse();

    [Fact]
    public void IsEffectivelyEmpty_CheckedCheckbox_ReturnsFalse()
        => HeartbeatAction.IsEffectivelyEmpty("- [x] Done task").ShouldBeFalse();

    [Fact]
    public void IsEffectivelyEmpty_MixedEmptyAndFilledCheckboxes_ReturnsFalse()
        => HeartbeatAction.IsEffectivelyEmpty("- [ ] \n- [x] Done\n- [ ]").ShouldBeFalse();

    // -----------------------------------------------------------------
    // Phase 4: HEARTBEAT.md pre-check — ExecuteAsync integration
    // -----------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_EmptyHeartbeatFile_SkipsExecution()
    {
        var action = new HeartbeatAction();
        var trigger = new Mock<IInternalTrigger>();
        var registry = new Mock<IAgentRegistry>();
        var workspace = new Mock<IAgentWorkspaceManager>();

        trigger.SetupGet(v => v.Type).Returns(TriggerType.Heartbeat);
        registry.Setup(v => v.Get(AgentId.From("agent-a"))).Returns((AgentDescriptor?)null);
        workspace.Setup(v => v.GetWorkspacePath("agent-a"))
            .Returns(Path.Combine(Path.GetTempPath(), "botnexus-test-nonexistent-" + Guid.NewGuid()));

        var services = new ServiceCollection()
            .AddSingleton(registry.Object)
            .AddSingleton<IInternalTrigger>(trigger.Object)
            .AddSingleton(workspace.Object)
            .BuildServiceProvider();
        var context = CreateContext(services);

        await action.ExecuteAsync(context);

        // HEARTBEAT.md doesn't exist in the temp path — should skip
        trigger.Verify(v => v.CreateSessionAsync(
            It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<InternalTriggerRequest?>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_NoWorkspaceManager_ProceedsNormally()
    {
        // When IAgentWorkspaceManager is not registered, the pre-check is skipped entirely
        var action = new HeartbeatAction();
        var trigger = new Mock<IInternalTrigger>();
        var registry = new Mock<IAgentRegistry>();
        var sessionId = SessionId.From("heartbeat:agent-a:20260101000000:nomanager");

        trigger.SetupGet(v => v.Type).Returns(TriggerType.Heartbeat);
        trigger.Setup(v => v.CreateSessionAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<InternalTriggerRequest?>()))
            .ReturnsAsync(sessionId);
        registry.Setup(v => v.Get(AgentId.From("agent-a"))).Returns((AgentDescriptor?)null);

        var services = new ServiceCollection()
            .AddSingleton(registry.Object)
            .AddSingleton<IInternalTrigger>(trigger.Object)
            // no IAgentWorkspaceManager registered
            .BuildServiceProvider();
        var context = CreateContext(services);

        await action.ExecuteAsync(context);

        trigger.Verify(v => v.CreateSessionAsync(
            It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<InternalTriggerRequest?>()),
            Times.Once);
    }

    [Fact]
    public async Task ReadHeartbeatFileAsync_MissingFile_ReturnsNull()
    {
        var workspace = new Mock<IAgentWorkspaceManager>();
        workspace.Setup(v => v.GetWorkspacePath("agent-a"))
            .Returns(Path.Combine(Path.GetTempPath(), "botnexus-test-nonexistent-" + Guid.NewGuid()));

        var result = await HeartbeatAction.ReadHeartbeatFileAsync(workspace.Object, "agent-a", CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ReadHeartbeatFileAsync_ExistingFile_ReturnsContent()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "botnexus-test-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        var heartbeatPath = Path.Combine(tempDir, "HEARTBEAT.md");
        await File.WriteAllTextAsync(heartbeatPath, "- [ ] Deploy the thing");

        var workspace = new Mock<IAgentWorkspaceManager>();
        workspace.Setup(v => v.GetWorkspacePath("agent-a")).Returns(tempDir);

        try
        {
            var result = await HeartbeatAction.ReadHeartbeatFileAsync(workspace.Object, "agent-a", CancellationToken.None);
            result.ShouldBe("- [ ] Deploy the thing");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static CronExecutionContext CreateContext(
        IServiceProvider services,
        string? agentId = "agent-a",
        string? message = "Heartbeat ping")
        => new()
        {
            Job = new CronJob
            {
                Id = "heartbeat:agent-a",
                Name = "Heartbeat",
                Schedule = "*/30 * * * *",
                ActionType = "heartbeat",
                AgentId = agentId,
                Message = message,
                Enabled = true,
                System = true,
                CreatedBy = "system:heartbeat",
                CreatedAt = DateTimeOffset.UtcNow
            },
            RunId = "run-1",
            TriggeredAt = DateTimeOffset.UtcNow,
            TriggerType = CronTriggerType.Scheduled,
            Services = services
        };

    private static AgentDescriptor CreateDescriptorWithQuietHours(QuietHoursConfig quietHours)
        => new()
        {
            AgentId = AgentId.From("agent-a"),
            DisplayName = "Agent A",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            Heartbeat = new HeartbeatAgentConfig
            {
                Enabled = true,
                QuietHours = quietHours
            }
        };
}
