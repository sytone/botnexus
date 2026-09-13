using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Hooks;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Hooks;
using NSubstitute;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Gateway.Tests;

public sealed class DiagnosticExecutionOriginTests
{
    [Fact]
    public async Task WorkspaceContextBuilder_RuntimePromptHookCarriesAuthoritativeExecutionOrigin()
    {
        var agentId = AgentId.From("origin-agent");
        var sessionId = SessionId.From("origin-session");
        var conversationId = ConversationId.From("origin-conversation");
        var workspace = Path.Combine(Path.GetTempPath(), "botnexus-origin-tests", Guid.NewGuid().ToString("N"), "workspace");
        var fileSystem = new MockFileSystem();
        fileSystem.AddDirectory(workspace);
        fileSystem.AddFile(Path.Combine(workspace, "AGENTS.md"), new MockFileData("origin test"));

        var conversation = new Conversation
        {
            ConversationId = conversationId,
            AgentId = agentId,
            ActiveSessionId = sessionId
        };
        var session = new GatewaySession
        {
            SessionId = sessionId,
            AgentId = agentId,
            ConversationId = conversationId,
            ChannelType = ChannelKey.From("signalr")
        };
        var conversations = Substitute.For<IConversationStore>();
        conversations.GetAsync(conversationId, Arg.Any<CancellationToken>()).Returns(conversation);
        var sessions = Substitute.For<ISessionStore>();
        sessions.GetAsync(sessionId, Arg.Any<CancellationToken>()).Returns(session);

        BeforePromptBuildEvent? captured = null;
        var dispatcher = new HookDispatcher();
        dispatcher.Register<BeforePromptBuildEvent, BeforePromptBuildResult>(
            new CapturePromptHook(evt => captured = evt));
        var builder = new WorkspaceContextBuilder(
            new StubWorkspaceManager(workspace), fileSystem, conversations, sessions, dispatcher);
        var descriptor = CreateDescriptor(agentId);
        using var activity = new System.Diagnostics.Activity("origin-test").Start();

        await builder.BuildSystemPromptAsync(
            descriptor,
            new AgentExecutionContext
            {
                SessionId = sessionId,
                Parameters = new Dictionary<string, object?> { ["channel"] = "signalr" }
            });

        captured.ShouldNotBeNull();
        var origin = captured!.Origin;
        origin.Kind.ShouldBe(DiagnosticOriginKind.Execution);
        origin.Trigger.ShouldBe(DiagnosticTrigger.PromptConstruction);
        origin.AgentId.ShouldBe(agentId);
        origin.ConversationId.ShouldBe(conversationId);
        origin.SessionId.ShouldBe(sessionId);
        origin.Channel.ShouldBe("signalr");
        origin.SourceComponent.ShouldBe("WorkspaceContextBuilder");
        origin.Category.ShouldBe("prompt-hook");
        origin.TraceId.ShouldBe(activity.TraceId.ToString());
        origin.SpanId.ShouldBe(activity.SpanId.ToString());
        origin.CorrelationId.ShouldBe(activity.TraceId.ToString());
        origin.ToolCallId.ShouldBeNull();
        origin.RunId.ShouldBeNull();
    }

    [Fact]
    public async Task WorkspaceContextBuilder_DescriptorOnlyPromptHookCarriesExplicitIncompleteOrigin()
    {
        var agentId = AgentId.From("descriptor-agent");
        var workspace = Path.Combine(Path.GetTempPath(), "botnexus-origin-tests", Guid.NewGuid().ToString("N"), "workspace");
        var fileSystem = new MockFileSystem();
        fileSystem.AddDirectory(workspace);

        BeforePromptBuildEvent? captured = null;
        var dispatcher = new HookDispatcher();
        dispatcher.Register<BeforePromptBuildEvent, BeforePromptBuildResult>(
            new CapturePromptHook(evt => captured = evt));
        var builder = new WorkspaceContextBuilder(new StubWorkspaceManager(workspace), fileSystem, dispatcher);

        await builder.BuildSystemPromptAsync(CreateDescriptor(agentId));

        captured.ShouldNotBeNull();
        captured!.Origin.Kind.ShouldBe(DiagnosticOriginKind.DescriptorOnly);
        captured.Origin.Trigger.ShouldBe(DiagnosticTrigger.PromptConstruction);
        captured.Origin.AgentId.ShouldBe(agentId);
        captured.Origin.ConversationId.ShouldBeNull();
        captured.Origin.SessionId.ShouldBeNull();
        captured.Origin.RunId.ShouldBeNull();
        captured.Origin.ToolCallId.ShouldBeNull();
        captured.Origin.Channel.ShouldBeNull();
        captured.Origin.SourceComponent.ShouldBe("WorkspaceContextBuilder");
        captured.Origin.Category.ShouldBe("prompt-hook");
    }

    [Fact]
    public void ToolInvocationOriginRequiresAndPreservesActualToolCallId()
    {
        var origin = DiagnosticExecutionOrigin.ForToolInvocation(
            AgentId.From("tool-agent"),
            SessionId.From("tool-session"),
            ConversationId.From("tool-conversation"),
            "call-actual",
            "signalr");

        origin.Kind.ShouldBe(DiagnosticOriginKind.Execution);
        origin.Trigger.ShouldBe(DiagnosticTrigger.ToolInvocation);
        origin.ToolCallId.ShouldBe("call-actual");
        origin.SourceComponent.ShouldBe("InProcessIsolationStrategy");
        origin.Category.ShouldBe("tool-hook");
        Should.Throw<ArgumentException>(() => DiagnosticExecutionOrigin.ForToolInvocation(
            AgentId.From("tool-agent"),
            SessionId.From("tool-session"),
            null,
            " ",
            null));
    }

    private static AgentDescriptor CreateDescriptor(AgentId agentId) => new()
    {
        AgentId = agentId,
        DisplayName = agentId.Value,
        ModelId = "test-model",
        ApiProvider = "test-provider"
    };

    private sealed class CapturePromptHook(Action<BeforePromptBuildEvent> capture)
        : IHookHandler<BeforePromptBuildEvent, BeforePromptBuildResult>
    {
        public int Priority => 0;

        public Task<BeforePromptBuildResult?> HandleAsync(
            BeforePromptBuildEvent hookEvent,
            CancellationToken ct = default)
        {
            capture(hookEvent);
            return Task.FromResult<BeforePromptBuildResult?>(null);
        }
    }

    private sealed class StubWorkspaceManager(string workspacePath) : IAgentWorkspaceManager
    {
        public string GetWorkspacePath(string agentName) => workspacePath;

        public Task<AgentWorkspace> LoadWorkspaceAsync(string agentName, CancellationToken ct = default)
            => Task.FromResult(new AgentWorkspace(agentName, string.Empty, string.Empty, string.Empty, string.Empty));

        public Task SaveMemoryAsync(string agentName, string content, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task SaveMemoryAsync(string agentName, string? filePath, string content, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task SaveMemoryAsync(
            string agentName,
            string? filePath,
            string content,
            string? memoryPathOverride,
            CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
