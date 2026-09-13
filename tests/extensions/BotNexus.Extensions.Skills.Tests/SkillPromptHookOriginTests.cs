using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Hooks;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Skills.Tests;

public sealed class SkillPromptHookOriginTests
{
    [Fact]
    public async Task HandleAsync_ScopesDiscoveryAsPromptConstructionWithoutInvocationAttribution()
    {
        var logger = new ScopeCaptureLogger<SkillPromptHookHandler>();
        var handler = new SkillPromptHookHandler(new StubWorkspaceManager(), logger);
        var agentId = AgentId.From("skills-origin-agent");
        var origin = DiagnosticExecutionOrigin.ForPromptConstruction(
            agentId,
            SessionId.From("skills-origin-session"),
            ConversationId.From("skills-origin-conversation"),
            "signalr");
        var hookEvent = new BeforePromptBuildEvent(
            agentId,
            new AgentDescriptor
            {
                AgentId = agentId,
                DisplayName = "Skills Origin Agent",
                ModelId = "test-model",
                ApiProvider = "test-provider"
            },
            "prompt",
            [],
            origin);

        await handler.HandleAsync(hookEvent);

        logger.Scope.ShouldNotBeNull();
        logger.Scope!["DiagnosticTrigger"].ShouldBe(nameof(DiagnosticTrigger.PromptConstruction));
        logger.Scope["AgentId"].ShouldBe(agentId.Value);
        logger.Scope["ConversationId"].ShouldBe("skills-origin-conversation");
        logger.Scope["SessionId"].ShouldBe("skills-origin-session");
        logger.Scope["Channel"].ShouldBe("signalr");
        logger.Scope["ToolCallId"].ShouldBeNull();
    }

    private sealed class ScopeCaptureLogger<T> : ILogger<T>
    {
        public IReadOnlyDictionary<string, object?>? Scope { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            Scope = state as IReadOnlyDictionary<string, object?>;
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();
        public void Dispose() { }
    }

    private sealed class StubWorkspaceManager : IAgentWorkspaceManager
    {
        public string GetWorkspacePath(string agentId) => Path.Combine(Path.GetTempPath(), "missing-skill-origin-workspace");

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
