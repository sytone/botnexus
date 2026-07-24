using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Unit tests for the portal start-conversation orchestration (issue #2036). These lock the
/// behaviours that cannot be asserted from a Razor component: the strict call ORDER
/// (create -> persist override -> send), the persisted per-conversation override semantics, and
/// the rule that no failure path may return a navigable conversation id.
/// </summary>
public sealed class StartConversationServiceTests
{
    private readonly IAgentInteractionService _interaction = Substitute.For<IAgentInteractionService>();
    private readonly IGatewayRestClient _rest = Substitute.For<IGatewayRestClient>();
    private readonly List<string> _calls = [];

    private StartConversationService CreateSut() =>
        new(_interaction, _rest, NullLogger<StartConversationService>.Instance);

    private static ConversationResponseDto Conversation(string id, string agentId, string? modelOverride = null) =>
        new(
            ConversationId: id,
            AgentId: agentId,
            Title: "New conversation",
            IsDefault: false,
            Status: "Active",
            ActiveSessionId: null,
            Bindings: [],
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            ModelOverride: modelOverride);

    /// <summary>Records create/override/send into <see cref="_calls"/> so ordering can be asserted.</summary>
    private void ArrangeHappyPath(string agentId = "agent-1", string conversationId = "conv-1")
    {
        _interaction.CreateConversationAsync(agentId, Arg.Any<string?>(), Arg.Any<bool>())
            .Returns(_ => { _calls.Add("create"); return conversationId; });

        _rest.SetConversationOverrideAsync(conversationId, Arg.Any<SetConversationOverrideRequestDto>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _calls.Add("override");
                return Task.FromResult<ConversationResponseDto?>(
                    Conversation(conversationId, agentId, ci.Arg<SetConversationOverrideRequestDto>().Model));
            });

        _interaction.SendMessageAsync(agentId, Arg.Any<string>())
            .Returns(_ => { _calls.Add("send"); return Task.CompletedTask; });
    }

    [Fact]
    public async Task Creates_applies_override_then_sends_in_that_order()
    {
        ArrangeHappyPath();
        var sut = CreateSut();

        var result = await sut.StartAsync(new StartConversationRequest("agent-1", "hello", "claude-opus-4", "gpt-4o"));

        result.Success.ShouldBeTrue();
        _calls.ShouldBe(["create", "override", "send"]);
    }

    [Fact]
    public async Task Persists_selected_model_as_conversation_override_before_first_message()
    {
        ArrangeHappyPath();
        var sut = CreateSut();

        await sut.StartAsync(new StartConversationRequest("agent-1", "hello", "claude-opus-4", "gpt-4o"));

        // Persisted per-conversation override (PUT /conversations/{id}/override), not a one-shot
        // decoration of the outgoing message.
        await _rest.Received(1).SetConversationOverrideAsync(
            "conv-1",
            Arg.Is<SetConversationOverrideRequestDto>(r => r.Model == "claude-opus-4"),
            Arg.Any<CancellationToken>());
        _calls.IndexOf("override").ShouldBeLessThan(_calls.IndexOf("send"));
    }

    [Fact]
    public async Task Returns_agent_and_conversation_identity_for_navigation()
    {
        ArrangeHappyPath();
        var sut = CreateSut();

        var result = await sut.StartAsync(new StartConversationRequest("agent-1", "hello", "claude-opus-4", "gpt-4o"));

        result.AgentId.ShouldBe("agent-1");
        result.ConversationId.ShouldBe("conv-1");
        result.AppliedModelOverride.ShouldBe("claude-opus-4");
        result.Error.ShouldBeNull();
    }

    [Fact]
    public async Task Sends_the_first_message_into_the_new_conversation()
    {
        ArrangeHappyPath();
        var sut = CreateSut();

        await sut.StartAsync(new StartConversationRequest("agent-1", "hello there", "claude-opus-4", "gpt-4o"));

        await _interaction.Received(1).CreateConversationAsync("agent-1", null, true);
        await _interaction.Received(1).SendMessageAsync("agent-1", "hello there");
    }

    [Fact]
    public async Task No_override_written_when_no_model_selected()
    {
        ArrangeHappyPath();
        var sut = CreateSut();

        var result = await sut.StartAsync(new StartConversationRequest("agent-1", "hello", SelectedModel: null, AgentDefaultModel: "gpt-4o"));

        result.Success.ShouldBeTrue();
        result.AppliedModelOverride.ShouldBeNull();
        await _rest.DidNotReceiveWithAnyArgs().SetConversationOverrideAsync(default!, default!, default);
        _calls.ShouldBe(["create", "send"]);
    }

    [Theory]
    [InlineData("gpt-4o")]
    [InlineData("GPT-4O")]
    [InlineData("  gpt-4o  ")]
    public async Task No_override_written_when_selection_equals_agent_default(string selected)
    {
        ArrangeHappyPath();
        var sut = CreateSut();

        var result = await sut.StartAsync(new StartConversationRequest("agent-1", "hello", selected, "gpt-4o"));

        result.Success.ShouldBeTrue();
        result.AppliedModelOverride.ShouldBeNull();
        await _rest.DidNotReceiveWithAnyArgs().SetConversationOverrideAsync(default!, default!, default);
    }

    [Fact]
    public async Task Failed_creation_returns_no_navigable_conversation_and_never_sends()
    {
        _interaction.CreateConversationAsync("agent-1", Arg.Any<string?>(), Arg.Any<bool>())
            .Returns((string?)null);
        var sut = CreateSut();

        var result = await sut.StartAsync(new StartConversationRequest("agent-1", "hello", "claude-opus-4", "gpt-4o"));

        result.Success.ShouldBeFalse();
        result.ConversationId.ShouldBeNull();
        result.Error.ShouldNotBeNullOrWhiteSpace();
        await _interaction.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!);
    }

    [Fact]
    public async Task Creation_exception_returns_failure_not_navigable()
    {
        _interaction.CreateConversationAsync("agent-1", Arg.Any<string?>(), Arg.Any<bool>())
            .Returns<string?>(_ => throw new InvalidOperationException("boom"));
        var sut = CreateSut();

        var result = await sut.StartAsync(new StartConversationRequest("agent-1", "hello"));

        result.Success.ShouldBeFalse();
        result.ConversationId.ShouldBeNull();
        await _interaction.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!);
    }

    [Fact]
    public async Task Failed_override_fails_closed_and_does_not_send_on_the_agent_default()
    {
        _interaction.CreateConversationAsync("agent-1", Arg.Any<string?>(), Arg.Any<bool>()).Returns("conv-1");
        _rest.SetConversationOverrideAsync("conv-1", Arg.Any<SetConversationOverrideRequestDto>(), Arg.Any<CancellationToken>())
            .Returns((ConversationResponseDto?)null);
        var sut = CreateSut();

        var result = await sut.StartAsync(new StartConversationRequest("agent-1", "hello", "claude-opus-4", "gpt-4o"));

        result.Success.ShouldBeFalse();
        result.ConversationId.ShouldBeNull();
        await _interaction.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!);
    }

    [Fact]
    public async Task Override_exception_returns_failure_and_does_not_send()
    {
        _interaction.CreateConversationAsync("agent-1", Arg.Any<string?>(), Arg.Any<bool>()).Returns("conv-1");
        _rest.SetConversationOverrideAsync("conv-1", Arg.Any<SetConversationOverrideRequestDto>(), Arg.Any<CancellationToken>())
            .Returns<ConversationResponseDto?>(_ => throw new HttpRequestException("network"));
        var sut = CreateSut();

        var result = await sut.StartAsync(new StartConversationRequest("agent-1", "hello", "claude-opus-4", "gpt-4o"));

        result.Success.ShouldBeFalse();
        result.ConversationId.ShouldBeNull();
        await _interaction.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!);
    }

    [Fact]
    public async Task Failed_send_returns_no_navigable_conversation()
    {
        _interaction.CreateConversationAsync("agent-1", Arg.Any<string?>(), Arg.Any<bool>()).Returns("conv-1");
        _interaction.SendMessageAsync("agent-1", Arg.Any<string>())
            .Returns<Task>(_ => throw new InvalidOperationException("hub down"));
        var sut = CreateSut();

        var result = await sut.StartAsync(new StartConversationRequest("agent-1", "hello"));

        result.Success.ShouldBeFalse();
        result.ConversationId.ShouldBeNull();
        result.Error.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("", "hello")]
    [InlineData("   ", "hello")]
    [InlineData("agent-1", "")]
    [InlineData("agent-1", "   ")]
    public async Task Invalid_input_is_rejected_without_touching_the_gateway(string agentId, string message)
    {
        var sut = CreateSut();

        var result = await sut.StartAsync(new StartConversationRequest(agentId, message));

        result.Success.ShouldBeFalse();
        result.ConversationId.ShouldBeNull();
        await _interaction.DidNotReceiveWithAnyArgs().CreateConversationAsync(default!, default, default);
        await _rest.DidNotReceiveWithAnyArgs().SetConversationOverrideAsync(default!, default!, default);
    }
}
