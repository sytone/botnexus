using System.Runtime.CompilerServices;
using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.AgentExchange;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Tools;
using Microsoft.Extensions.Logging;
using Moq;

namespace BotNexus.Gateway.Tests.Tools;

public sealed class AgentConverseToolTests
{
    [Fact]
    public void Tool_HasExpectedNameAndLabel()
    {
        var tool = new AgentConverseTool(Mock.Of<IAgentExchangeService>(), new InMemorySessionStore(), AgentId.From("test-agent"), SessionId.From("session-1"));
        tool.Name.ShouldBe("agent_converse");
        tool.Label.ShouldBe("Agent Converse");
    }

    [Fact]
    public void Tool_DeclaresTenMinuteDefaultTimeout()
    {
        var tool = CreateTool();

        tool.DefaultTimeout.ShouldBe(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void Definition_AdvertisesBoundedTimeoutOverride()
    {
        var tool = CreateTool();

        var timeout = tool.Definition.Parameters
            .GetProperty("properties").GetProperty("timeoutSeconds");

        timeout.GetProperty("minimum").GetInt32().ShouldBe(1);
        timeout.GetProperty("maximum").GetInt32().ShouldBe(1800);
        timeout.GetProperty("default").GetInt32().ShouldBe(600);
        timeout.GetProperty("description").GetString().ShouldNotBeNull().ShouldContain("30 minutes");
    }

    /// <summary>
    /// #3577 AC1/AC2/AC3: exhausting the caller's own <c>timeoutSeconds</c> must surface a
    /// structured, explicitly-worded timeout result naming this side as the canceller and the
    /// elapsed time against the budget - never the bare .NET <c>A task was canceled.</c> default.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenTimeoutOverrideExpires_ReturnsStructuredTimeoutResult()
    {
        using var callerCancellation = new CancellationTokenSource();
        var service = new Mock<IAgentExchangeService>();
        service.Setup(s => s.ConverseAsync(It.IsAny<AgentExchangeRequest>(), It.IsAny<CancellationToken>()))
            .Returns<AgentExchangeRequest, CancellationToken>((_, token) => WaitForCancellationAsync(token));
        var tool = new AgentConverseTool(service.Object, new InMemorySessionStore(), AgentId.From("test-agent"), SessionId.From("session-1"));

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["agentId"] = "agent-c",
            ["message"] = "Take your time",
            ["timeoutSeconds"] = 1
        }, callerCancellation.Token);

        var text = ReadText(result);
        text.ShouldNotContain("A task was canceled");
        using var payload = JsonDocument.Parse(text);
        var root = payload.RootElement;
        root.GetProperty("cancelled").GetBoolean().ShouldBeTrue();
        root.GetProperty("cancellationCause").GetString().ShouldBe("timeout");
        root.GetProperty("cancelledBy").GetString().ShouldBe("caller");
        root.GetProperty("timeoutSeconds").GetInt32().ShouldBe(1);
        root.GetProperty("elapsedSeconds").GetDouble().ShouldBeGreaterThan(0d);
        root.GetProperty("targetAgentId").GetString().ShouldBe("agent-c");
        root.GetProperty("message").GetString().ShouldNotBeNull()
            .ShouldContain("timed out");

        callerCancellation.IsCancellationRequested.ShouldBeFalse();
    }

    /// <summary>
    /// #3577 AC1/AC4: a cancellation the caller's budget did not cause must be reported as such and
    /// must name the target's state, so the caller can tell "the peer was unavailable" apart from
    /// "I did not give it long enough".
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenTargetIsUnregistered_NamesTargetStateInsteadOfBareCancellation()
    {
        using var callerCancellation = new CancellationTokenSource();
        using var foreignCancellation = new CancellationTokenSource();
        await foreignCancellation.CancelAsync();

        var service = new Mock<IAgentExchangeService>();
        service.Setup(s => s.ConverseAsync(It.IsAny<AgentExchangeRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(foreignCancellation.Token));

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Contains(It.IsAny<AgentId>())).Returns(false);

        var tool = new AgentConverseTool(
            service.Object,
            new InMemorySessionStore(),
            AgentId.From("test-agent"),
            SessionId.From("session-1"),
            agentRegistry: registry.Object);

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["agentId"] = "ghost-agent",
            ["message"] = "Are you there",
            ["timeoutSeconds"] = 1800
        }, callerCancellation.Token);

        var text = ReadText(result);
        text.ShouldNotContain("A task was canceled");
        using var payload = JsonDocument.Parse(text);
        var root = payload.RootElement;
        root.GetProperty("cancelled").GetBoolean().ShouldBeTrue();
        root.GetProperty("cancellationCause").GetString().ShouldBe("targetUnavailable");
        root.GetProperty("cancelledBy").GetString().ShouldBe("target");
        root.GetProperty("targetState").GetString().ShouldBe("unregistered");
        root.GetProperty("timeoutSeconds").GetInt32().ShouldBe(1800);
        root.GetProperty("retryAdvised").GetBoolean().ShouldBeFalse();
        root.GetProperty("message").GetString().ShouldNotBeNull()
            .ShouldContain("unregistered");
    }

    /// <summary>
    /// #3577 AC4: a busy target is a retryable state and must be named distinctly from an
    /// unregistered one, otherwise the caller cannot choose between waiting and giving up.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenTargetIsBusy_NamesBusyStateAndAdvisesRetry()
    {
        using var callerCancellation = new CancellationTokenSource();
        using var foreignCancellation = new CancellationTokenSource();
        await foreignCancellation.CancelAsync();

        var service = new Mock<IAgentExchangeService>();
        service.Setup(s => s.ConverseAsync(It.IsAny<AgentExchangeRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(foreignCancellation.Token));

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Contains(It.IsAny<AgentId>())).Returns(true);

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetAllInstances()).Returns(
        [
            new AgentInstance
            {
                InstanceId = "busy-1",
                AgentId = AgentId.From("agent-c"),
                SessionId = SessionId.From("target-session"),
                Status = AgentInstanceStatus.Running,
                IsolationStrategy = "in-process"
            }
        ]);

        var tool = new AgentConverseTool(
            service.Object,
            new InMemorySessionStore(),
            AgentId.From("test-agent"),
            SessionId.From("session-1"),
            agentRegistry: registry.Object,
            agentSupervisor: supervisor.Object);

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["agentId"] = "agent-c",
            ["message"] = "Are you there",
            ["timeoutSeconds"] = 300
        }, callerCancellation.Token);

        var text = ReadText(result);
        text.ShouldNotContain("A task was canceled");
        using var payload = JsonDocument.Parse(text);
        var root = payload.RootElement;
        root.GetProperty("cancellationCause").GetString().ShouldBe("targetUnavailable");
        root.GetProperty("targetState").GetString().ShouldBe("busy");
        root.GetProperty("retryAdvised").GetBoolean().ShouldBeTrue();
        root.GetProperty("message").GetString().ShouldNotBeNull()
            .ShouldContain("busy");
    }

    /// <summary>
    /// #3577 AC5: every cancellation must be correlatable from a single occurrence - caller session
    /// id, target agent id and the tool call id that ties the log line to the transcript row.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenCancelled_LogsCorrelationIdentifiers()
    {
        using var callerCancellation = new CancellationTokenSource();
        var service = new Mock<IAgentExchangeService>();
        service.Setup(s => s.ConverseAsync(It.IsAny<AgentExchangeRequest>(), It.IsAny<CancellationToken>()))
            .Returns<AgentExchangeRequest, CancellationToken>((_, token) => WaitForCancellationAsync(token));

        var logger = new CapturingLogger();
        var tool = new AgentConverseTool(
            service.Object,
            new InMemorySessionStore(),
            AgentId.From("test-agent"),
            SessionId.From("session-1"),
            logger: logger);

        await tool.ExecuteAsync("call-42", new Dictionary<string, object?>
        {
            ["agentId"] = "agent-c",
            ["message"] = "Take your time",
            ["timeoutSeconds"] = 1
        }, callerCancellation.Token);

        var entry = logger.Entries.ShouldHaveSingleItem();
        entry.ShouldContain("session-1");
        entry.ShouldContain("agent-c");
        entry.ShouldContain("call-42");
    }

    [Fact]
    public async Task ExecuteAsync_WhenCallerCancels_PropagatesCallerCancellation()
    {
        using var callerCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var service = new Mock<IAgentExchangeService>();
        service.Setup(s => s.ConverseAsync(It.IsAny<AgentExchangeRequest>(), It.IsAny<CancellationToken>()))
            .Returns<AgentExchangeRequest, CancellationToken>((_, token) => WaitForCancellationAsync(token));
        var tool = new AgentConverseTool(service.Object, new InMemorySessionStore(), AgentId.From("test-agent"), SessionId.From("session-1"));

        Func<Task> action = () => tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["agentId"] = "agent-c",
            ["message"] = "Take your time",
            ["timeoutSeconds"] = 1800
        }, callerCancellation.Token);

        await action.ShouldThrowAsync<OperationCanceledException>();
        callerCancellation.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task PrepareArgumentsAsync_WhenTimeoutIsOmitted_UsesTenMinuteBudget()
    {
        var tool = CreateTool();

        var prepared = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["agentId"] = "agent-c",
            ["message"] = "Take your time"
        });

        prepared["timeoutSeconds"].ShouldBe(600);
        prepared["timeout"].ShouldBe(600);
    }

    [Fact]
    public async Task PrepareArgumentsAsync_WhenTimeoutOverrideIsValid_UsesOverride()
    {
        var tool = CreateTool();

        var prepared = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["agentId"] = "agent-c",
            ["message"] = "Take your time",
            ["timeoutSeconds"] = 240
        });

        prepared["timeoutSeconds"].ShouldBe(240);
        prepared["timeout"].ShouldBe(240);
    }

    [Fact]
    public async Task PrepareArgumentsAsync_WhenTimeoutExceedsMaximum_ClampsExecutorBudget()
    {
        var tool = CreateTool();

        var prepared = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["agentId"] = "agent-c",
            ["message"] = "Take your time",
            ["timeoutSeconds"] = 99999
        });

        prepared["timeoutSeconds"].ShouldBe(1800);
        prepared["timeout"].ShouldBe(1800);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task PrepareArgumentsAsync_WhenTimeoutIsBelowMinimum_Throws(int timeoutSeconds)
    {
        var tool = CreateTool();

        Func<Task> action = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["agentId"] = "agent-c",
            ["message"] = "Take your time",
            ["timeoutSeconds"] = timeoutSeconds
        });

        var exception = await action.ShouldThrowAsync<ArgumentOutOfRangeException>();
        exception.Message.ShouldContain("at least 1 second");
    }

    [Fact]
    public async Task PrepareArgumentsAsync_WhenTimeoutIsNotAnInteger_Throws()
    {
        var tool = CreateTool();

        Func<Task> action = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["agentId"] = "agent-c",
            ["message"] = "Take your time",
            ["timeoutSeconds"] = "eventually"
        });

        var exception = await action.ShouldThrowAsync<ArgumentException>();
        exception.Message.ShouldContain("must be an integer");
    }

    [Fact]
    public async Task PrepareArgumentsAsync_PreservesMaxTurns()
    {
        var tool = CreateTool();

        var prepared = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["agentId"] = "agent-c",
            ["message"] = "Take your time",
            ["maxTurns"] = 7
        });

        prepared["maxTurns"].ShouldBe(7);
    }

    // Streaming tool-call parsing boxes JSON integers as CLR long and non-integers as double
    // (StreamingJsonParser). Before issue #2415 the timeout switch only matched JsonElement/int/string,
    // so a valid boxed timeoutSeconds was rejected with "must be an integer". These reproduce that path.
    [Fact]
    public async Task PrepareArgumentsAsync_WhenTimeoutIsBoxedLong_UsesOverride()
    {
        var tool = CreateTool();

        var prepared = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["agentId"] = "agent-c",
            ["message"] = "Take your time",
            ["timeoutSeconds"] = 600L
        });

        prepared["timeoutSeconds"].ShouldBe(600);
        prepared["timeout"].ShouldBe(600);
    }

    [Fact]
    public async Task PrepareArgumentsAsync_WhenTimeoutIsIntegralDouble_UsesOverride()
    {
        var tool = CreateTool();

        var prepared = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["agentId"] = "agent-c",
            ["message"] = "Take your time",
            ["timeoutSeconds"] = 600.0
        });

        prepared["timeoutSeconds"].ShouldBe(600);
        prepared["timeout"].ShouldBe(600);
    }

    [Fact]
    public async Task PrepareArgumentsAsync_WhenTimeoutBoxedLongExceedsMaximum_ClampsExecutorBudget()
    {
        var tool = CreateTool();

        var prepared = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["agentId"] = "agent-c",
            ["message"] = "Take your time",
            ["timeoutSeconds"] = 5000L
        });

        prepared["timeoutSeconds"].ShouldBe(1800);
        prepared["timeout"].ShouldBe(1800);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public async Task PrepareArgumentsAsync_WhenTimeoutBoxedLongBelowMinimum_Throws(long timeoutSeconds)
    {
        var tool = CreateTool();

        Func<Task> action = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["agentId"] = "agent-c",
            ["message"] = "Take your time",
            ["timeoutSeconds"] = timeoutSeconds
        });

        var exception = await action.ShouldThrowAsync<ArgumentOutOfRangeException>();
        exception.Message.ShouldContain("at least 1 second");
    }

    [Fact]
    public async Task PrepareArgumentsAsync_WhenTimeoutIsFractionalDouble_Throws()
    {
        var tool = CreateTool();

        Func<Task> action = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["agentId"] = "agent-c",
            ["message"] = "Take your time",
            ["timeoutSeconds"] = 600.5
        });

        var exception = await action.ShouldThrowAsync<ArgumentException>();
        exception.Message.ShouldContain("must be an integer");
    }

    [Fact]
    public async Task PrepareArgumentsAsync_WhenTimeoutOverflowsInt32_Throws()
    {
        var tool = CreateTool();

        Func<Task> action = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["agentId"] = "agent-c",
            ["message"] = "Take your time",
            ["timeoutSeconds"] = 3_000_000_000L
        });

        var exception = await action.ShouldThrowAsync<ArgumentException>();
        exception.Message.ShouldContain("must be an integer");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task PrepareArgumentsAsync_WhenTimeoutIsNonFinite_Throws(double timeoutSeconds)
    {
        var tool = CreateTool();

        Func<Task> action = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["agentId"] = "agent-c",
            ["message"] = "Take your time",
            ["timeoutSeconds"] = timeoutSeconds
        });

        var exception = await action.ShouldThrowAsync<ArgumentException>();
        exception.Message.ShouldContain("must be an integer");
    }

    [Fact]
    public async Task ExecuteAsync_WhenMaxTurnsIsBoxedLong_HonoursValueInsteadOfDefaulting()
    {
        var (service, captured) = CreateCapturingService();
        var options = new AgentExchangeOptions { MaxTurnsCeiling = 30 };
        var tool = new AgentConverseTool(service.Object, new InMemorySessionStore(), AgentId.From("test-agent"), SessionId.From("session-1"), options);

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["agentId"] = "agent-c",
            ["message"] = "Iterate a few times",
            ["maxTurns"] = 5L
        });

        captured.Value.ShouldNotBeNull();
        captured.Value!.MaxTurns.ShouldBe(5);
    }

    // A JsonElement number wider than int (e.g. from a non-streaming provider that hands the tool a
    // JsonElement directly) must be rejected rather than silently truncated.
    [Fact]
    public async Task PrepareArgumentsAsync_WhenTimeoutIsJsonElementLongOverflow_Throws()
    {
        var tool = CreateTool();
        var element = JsonSerializer.SerializeToElement(9_000_000_000L);

        Func<Task> action = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["agentId"] = "agent-c",
            ["message"] = "Take your time",
            ["timeoutSeconds"] = element
        });

        var exception = await action.ShouldThrowAsync<ArgumentException>();
        exception.Message.ShouldContain("must be an integer");
    }

    [Fact]
    public async Task PrepareArgumentsAsync_WhenTimeoutIsJsonElementIntegralDouble_UsesOverride()
    {
        var tool = CreateTool();
        var element = JsonSerializer.SerializeToElement(240.0);

        var prepared = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["agentId"] = "agent-c",
            ["message"] = "Take your time",
            ["timeoutSeconds"] = element
        });

        prepared["timeoutSeconds"].ShouldBe(240);
        prepared["timeout"].ShouldBe(240);
    }

    [Fact]
    public async Task ExecuteAsync_WhenMaxTurnsIsIntegralDouble_HonoursValue()
    {
        var (service, captured) = CreateCapturingService();
        var options = new AgentExchangeOptions { MaxTurnsCeiling = 30 };
        var tool = new AgentConverseTool(service.Object, new InMemorySessionStore(), AgentId.From("test-agent"), SessionId.From("session-1"), options);

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["agentId"] = "agent-c",
            ["message"] = "Iterate a few times",
            ["maxTurns"] = 6.0
        });

        captured.Value.ShouldNotBeNull();
        captured.Value!.MaxTurns.ShouldBe(6);
    }

    [Fact]
    public async Task ExecuteAsync_WhenMaxTurnsIsBoxedLongOverflow_FallsBackToDefault()
    {
        var (service, captured) = CreateCapturingService();
        var options = new AgentExchangeOptions { MaxTurnsCeiling = 30 };
        var tool = new AgentConverseTool(service.Object, new InMemorySessionStore(), AgentId.From("test-agent"), SessionId.From("session-1"), options);

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["agentId"] = "agent-c",
            ["message"] = "Iterate a few times",
            ["maxTurns"] = 9_000_000_000L
        });

        // Unparseable maxTurns falls back to the schema default of 1 (then clamped to >= 1).
        captured.Value.ShouldNotBeNull();
        captured.Value!.MaxTurns.ShouldBe(1);
    }

    [Fact]
    public async Task PrepareArgumentsAsync_WhenRequiredArgsMissing_Throws()
    {
        var tool = new AgentConverseTool(Mock.Of<IAgentExchangeService>(), new InMemorySessionStore(), AgentId.From("test-agent"), SessionId.From("session-1"));

        Func<Task> action = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?>());

        await action.ShouldThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ExecuteAsync_WhenSessionHasCallChain_ForwardsChainToAgentExchangeRequest()
    {
        AgentExchangeRequest? captured = null;
        var service = new Mock<IAgentExchangeService>();
        service.Setup(s => s.ConverseAsync(It.IsAny<AgentExchangeRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AgentExchangeRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new AgentExchangeResult
            {
                SessionId = SessionId.From("nova::agent-agent::leela::abc123"),
                ConversationId = ConversationId.Create(),
                Status = "sealed",
                Turns = 2,
                FinalResponse = "Done",
                Transcript = []
            });

        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync(SessionId.From("session-1"), AgentId.From("test-agent"));
        session.Metadata["callChain"] = new[] { "alpha", "test-agent" };
        await store.SaveAsync(session);

        var tool = new AgentConverseTool(service.Object, store, AgentId.From("test-agent"), SessionId.From("session-1"));
        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["agentId"] = "agent-c",
            ["message"] = "Review this plan"
        });

        captured.ShouldNotBeNull();
        captured!.CallChain.Select(id => id.Value).ShouldBe(new[] { "alpha", "test-agent" });
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoCallChain_UsesInitiatorAsDefaultChain()
    {
        AgentExchangeRequest? captured = null;
        var service = new Mock<IAgentExchangeService>();
        service.Setup(s => s.ConverseAsync(It.IsAny<AgentExchangeRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AgentExchangeRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new AgentExchangeResult
            {
                SessionId = SessionId.From("nova::agent-agent::leela::abc123"),
                ConversationId = ConversationId.Create(),
                Status = "sealed",
                Turns = 2,
                FinalResponse = "Done",
                Transcript = []
            });

        var store = new InMemorySessionStore();
        var tool = new AgentConverseTool(service.Object, store, AgentId.From("test-agent"), SessionId.From("session-1"));
        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["agentId"] = "agent-c",
            ["message"] = "Review this plan",
            ["maxTurns"] = 3
        });

        captured.ShouldNotBeNull();
        captured!.CallChain.ShouldHaveSingleItem().Value.ShouldBe("test-agent");
        captured.MaxTurns.ShouldBe(3);
        ReadText(result).ShouldContain("\"sessionId\"");
    }

    [Fact]
    public async Task ExecuteAsync_WhenMaxTurnsExceedsCeiling_ClampsToCeiling()
    {
        var (service, captured) = CreateCapturingService();
        var options = new AgentExchangeOptions { MaxTurnsCeiling = 10 };
        var tool = new AgentConverseTool(service.Object, new InMemorySessionStore(), AgentId.From("test-agent"), SessionId.From("session-1"), options);

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["agentId"] = "agent-c",
            ["message"] = "Run forever",
            ["maxTurns"] = 100000
        });

        captured.Value.ShouldNotBeNull();
        captured.Value!.MaxTurns.ShouldBe(10);
    }

    [Fact]
    public async Task ExecuteAsync_WhenMaxTurnsWithinCeiling_PassesThrough()
    {
        var (service, captured) = CreateCapturingService();
        var options = new AgentExchangeOptions { MaxTurnsCeiling = 30 };
        var tool = new AgentConverseTool(service.Object, new InMemorySessionStore(), AgentId.From("test-agent"), SessionId.From("session-1"), options);

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["agentId"] = "agent-c",
            ["message"] = "Iterate a few times",
            ["maxTurns"] = 7
        });

        captured.Value.ShouldNotBeNull();
        captured.Value!.MaxTurns.ShouldBe(7);
    }

    [Fact]
    public async Task ExecuteAsync_WhenMaxTurnsBelowOne_ClampsToOne()
    {
        var (service, captured) = CreateCapturingService();
        var tool = new AgentConverseTool(service.Object, new InMemorySessionStore(), AgentId.From("test-agent"), SessionId.From("session-1"));

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["agentId"] = "agent-c",
            ["message"] = "Hello",
            ["maxTurns"] = 0
        });

        captured.Value.ShouldNotBeNull();
        captured.Value!.MaxTurns.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoOptions_DefaultsToCeilingOfThirty()
    {
        var (service, captured) = CreateCapturingService();
        var tool = new AgentConverseTool(service.Object, new InMemorySessionStore(), AgentId.From("test-agent"), SessionId.From("session-1"));

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["agentId"] = "agent-c",
            ["message"] = "Run forever",
            ["maxTurns"] = 999
        });

        captured.Value.ShouldNotBeNull();
        captured.Value!.MaxTurns.ShouldBe(30);
    }

    [Fact]
    public void Definition_AdvertisesMaximumMatchingCeiling()
    {
        var options = new AgentExchangeOptions { MaxTurnsCeiling = 12 };
        var tool = new AgentConverseTool(Mock.Of<IAgentExchangeService>(), new InMemorySessionStore(), AgentId.From("test-agent"), SessionId.From("session-1"), options);

        var schema = tool.Definition.Parameters;
        var maximum = schema.GetProperty("properties").GetProperty("maxTurns").GetProperty("maximum").GetInt32();
        maximum.ShouldBe(12);
    }

    [Fact]
    public void Definition_SurfacesConverseAllowListGuidance()
    {
        var tool = new AgentConverseTool(Mock.Of<IAgentExchangeService>(), new InMemorySessionStore(), AgentId.From("test-agent"), SessionId.From("session-1"));

        tool.Definition.Description.ShouldContain("list_agents");
        tool.Definition.Description.ShouldContain("canConverse");

        var agentIdDescription = tool.Definition.Parameters
            .GetProperty("properties").GetProperty("agentId").GetProperty("description").GetString();
        agentIdDescription.ShouldNotBeNull();
        agentIdDescription.ShouldContain("canConverse");
    }

    private static AgentConverseTool CreateTool()
        => new(Mock.Of<IAgentExchangeService>(), new InMemorySessionStore(), AgentId.From("test-agent"), SessionId.From("session-1"));

    private static async Task<AgentExchangeResult> WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("The cancellation delay completed unexpectedly.");
    }

    private static (Mock<IAgentExchangeService> Service, StrongBox<AgentExchangeRequest?> Captured) CreateCapturingService()
    {
        var captured = new StrongBox<AgentExchangeRequest?>(null);
        var service = new Mock<IAgentExchangeService>();
        service.Setup(s => s.ConverseAsync(It.IsAny<AgentExchangeRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AgentExchangeRequest, CancellationToken>((request, _) => captured.Value = request)
            .ReturnsAsync(new AgentExchangeResult
            {
                SessionId = SessionId.From("nova::agent-agent::leela::abc123"),
                ConversationId = ConversationId.Create(),
                Status = "sealed",
                Turns = 2,
                FinalResponse = "Done",
                Transcript = []
            });
        return (service, captured);
    }

    private static string ReadText(AgentToolResult result)
        => result.Content.Single(item => item.Type == BotNexus.Agent.Core.Types.AgentToolContentType.Text).Value;

    /// <summary>
    /// Records formatted log messages so #3577 AC5 can assert on the correlation identifiers the
    /// cancellation path emits, rather than merely asserting that some log call happened.
    /// </summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add(formatter(state, exception));
    }
}
