using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Tools;
using Moq;

namespace BotNexus.Gateway.Tests.Tools;

/// <summary>
/// #2975: a successful <c>canvas</c> <c>render</c> must return a deep link to the canvas tab so the
/// agent can tell the user where to look, and must emit NOTHING rather than a guessed URL when the
/// portal's external base URL is not resolvable.
/// </summary>
/// <remarks>
/// The pairing matters. Asserting only that a link appears passes on an implementation that
/// fabricates one from a wildcard bind; asserting only that it is omitted passes on an
/// implementation that never emits a link at all. Both directions are pinned over the same tool.
/// </remarks>
public sealed class CanvasToolDeepLinkTests
{
    private const string Base = "https://portal.example.com";

    [Fact]
    public async Task Render_WithResolvableBaseUrl_ReturnsCanvasDeepLink()
    {
        var result = await RenderAsync(Options(Base), "agent-a", "conv-1");

        ReadText(result).ShouldContain(
            "https://portal.example.com/agent/agent-a/conversation/conv-1?tab=canvas");
    }

    [Fact]
    public async Task Render_WithUnsetBaseUrl_OmitsLinkAndStatesWhy()
    {
        var text = ReadText(await RenderAsync(Options(null), "agent-a", "conv-1"));

        // Nothing that could be mistaken for a link, and a reason the operator can act on.
        text.ShouldNotContain("http");
        text.ShouldNotContain("/agent/agent-a/conversation/");
        text.ShouldContain("gateway.publicBaseUrl");
        // The render itself still succeeded - the missing link must not read as a failed render.
        text.ShouldContain("Canvas rendered");
    }

    [Theory]
    [InlineData("http://+:5005")]
    [InlineData("http://*:5005")]
    [InlineData("http://0.0.0.0:5005")]
    [InlineData("http://[::]:5005")]
    public async Task Render_WithWildcardListenUrlOnly_OmitsLink(string listenUrl)
    {
        var text = ReadText(await RenderAsync(
            new CanvasToolOptions { PublicBaseUrl = null, ListenUrl = listenUrl },
            "agent-a",
            "conv-1"));

        text.ShouldNotContain("/agent/agent-a/conversation/");
        text.ShouldContain("gateway.publicBaseUrl");
    }

    [Fact]
    public async Task Render_FallsBackToConcreteListenUrl()
    {
        var text = ReadText(await RenderAsync(
            new CanvasToolOptions { PublicBaseUrl = null, ListenUrl = "http://localhost:5005" },
            "agent-a",
            "conv-1"));

        text.ShouldContain("http://localhost:5005/agent/agent-a/conversation/conv-1?tab=canvas");
    }

    [Fact]
    public async Task Render_PublicBaseUrlWinsOverListenUrl()
    {
        var text = ReadText(await RenderAsync(
            new CanvasToolOptions { PublicBaseUrl = Base, ListenUrl = "http://localhost:5005" },
            "agent-a",
            "conv-1"));

        text.ShouldContain(Base + "/agent/agent-a/conversation/conv-1?tab=canvas");
        text.ShouldNotContain("localhost");
    }

    [Fact]
    public async Task Render_WithoutConversation_OmitsLink()
    {
        var tool = new CanvasTool(AgentId.From("agent-a"), null, options: Options(Base));
        var text = ReadText(await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "render",
            ["html"] = "<p>x</p>",
        }));

        text.ShouldNotContain("?tab=canvas");
        text.ShouldContain("not bound to a conversation");
    }

    [Fact]
    public async Task Render_UrlEncodesAgentAndConversationIds()
    {
        var text = ReadText(await RenderAsync(Options(Base), "agent a/b", "conv?1#2"));

        text.ShouldContain("/agent/agent%20a%2Fb/conversation/conv%3F1%232?tab=canvas");
    }

    [Theory]
    [InlineData("clear")]
    [InlineData("get_state")]
    [InlineData("clear_state")]
    public async Task NonRenderActions_DoNotEmitALink(string action)
    {
        var tool = new CanvasTool(
            AgentId.From("agent-a"),
            ConversationId.From("conv-1"),
            options: Options(Base));

        var text = ReadText(await ExecuteAsync(tool, new Dictionary<string, object?> { ["action"] = action }));

        text.ShouldNotContain("?tab=canvas");
    }

    [Fact]
    public void ToolDescription_TellsTheAgentToSurfaceTheLink()
    {
        var description = new CanvasTool(AgentId.From("agent-a"), null).Definition.Description;

        // AC5: instruct BOTH halves - include the link, and still carry the substance in the reply.
        description.ShouldContain("canvasUrl");
        description.ShouldContain("reply");
    }

    private static CanvasToolOptions Options(string? publicBaseUrl)
        => new() { PublicBaseUrl = publicBaseUrl };

    private static async Task<AgentToolResult> RenderAsync(
        CanvasToolOptions options,
        string agentId,
        string conversationId)
    {
        var notifier = new Mock<IAgentCanvasNotifier>();
        var tool = new CanvasTool(
            AgentId.From(agentId),
            ConversationId.From(conversationId),
            canvasNotifiers: [notifier.Object],
            options: options);

        return await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "render",
            ["html"] = "<h1>Hello</h1>",
        });
    }

    private static async Task<AgentToolResult> ExecuteAsync(CanvasTool tool, Dictionary<string, object?> arguments)
    {
        var prepared = await tool.PrepareArgumentsAsync(arguments);
        return await tool.ExecuteAsync("call-canvas-deep-link-test", prepared);
    }

    private static string ReadText(AgentToolResult result)
        => string.Join("\n", result.Content
            .Where(content => content.Type == AgentToolContentType.Text)
            .Select(content => content.Value));
}
