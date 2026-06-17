using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests for issue #1481 (stretch criterion): the mobile <c>GlobalErrorBoundary</c>
/// must populate <see cref="ChannelErrorReportDto.SessionId"/> and
/// <see cref="ChannelErrorReportDto.ComponentStack"/> so the recoverable-error path
/// logs full session context (it previously omitted both).
/// </summary>
public sealed class MobileGlobalErrorBoundaryTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly IClientStateStore _store = Substitute.For<IClientStateStore>();
    private readonly IChannelErrorReporter _errorReporter = Substitute.For<IChannelErrorReporter>();

    public MobileGlobalErrorBoundaryTests()
    {
        _errorReporter.ReportAsync(Arg.Any<ChannelErrorReportDto>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _ctx.Services.AddSingleton(_store);
        _ctx.Services.AddSingleton(_errorReporter);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Reported_error_carries_active_session_and_component_stack()
    {
        var agent = new AgentState
        {
            AgentId = "agent-1",
            DisplayName = "Alpha",
            SessionId = "agent-session",
            ActiveConversationId = "conv-1"
        };
        agent.Conversations["conv-1"] = new ConversationState
        {
            ConversationId = "conv-1",
            Title = "C",
            ActiveSessionId = "conv-session"
        };
        _store.ActiveAgentId.Returns("agent-1");
        _store.GetAgent("agent-1").Returns(agent);

        _ctx.Render<GlobalErrorBoundary>(builder => builder.AddChildContent<ThrowingComponent>());

        // The boundary reports exactly one error with the active conversation's session
        // (preferred over the agent-level session) and a non-empty component stack.
        _errorReporter.Received(1).ReportAsync(
            Arg.Is<ChannelErrorReportDto>(r =>
                r.AgentId == "agent-1"
                && r.SessionId == "conv-session"
                && !string.IsNullOrEmpty(r.ComponentStack)
                && r.ComponentStack!.Contains("InvalidOperationException")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Reported_error_falls_back_to_agent_session_when_no_active_conversation()
    {
        var agent = new AgentState
        {
            AgentId = "agent-1",
            DisplayName = "Alpha",
            SessionId = "agent-session"
        };
        _store.ActiveAgentId.Returns("agent-1");
        _store.GetAgent("agent-1").Returns(agent);

        _ctx.Render<GlobalErrorBoundary>(builder => builder.AddChildContent<ThrowingComponent>());

        _errorReporter.Received(1).ReportAsync(
            Arg.Is<ChannelErrorReportDto>(r => r.SessionId == "agent-session"),
            Arg.Any<CancellationToken>());
    }

    /// <summary>A component that always throws during rendering for testing the error boundary.</summary>
    private sealed class ThrowingComponent : Microsoft.AspNetCore.Components.ComponentBase
    {
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
            => throw new InvalidOperationException("Test error from ThrowingComponent");
    }
}
