using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Services;
using BotNexus.Gateway.Channels;
using BotNexus.Gateway.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Services;

/// <summary>
/// Covers the channel-agnostic <c>ask_user</c> seam introduced by #2322: a simulated
/// non-SignalR channel renders a pending prompt and submits each of the three response
/// kinds (free text, structured selection, explicit cancel) through the single shared
/// resolution service, and an adapter that reports no interactive-prompt capability still
/// receives a usable text-degraded prompt.
/// </summary>
public sealed class AskUserPromptResolverTests
{
    private static readonly ConversationId Conversation = ConversationId.From("conversation-seam");

    private static AskUserPromptResolver CreateResolver(IAskUserResponseRegistry registry)
        => new(registry, NullLogger<AskUserPromptResolver>.Instance);

    private static AskUserRequest CreateRequest(string requestId) => new()
    {
        RequestId = requestId,
        ConversationId = Conversation,
        SessionId = SessionId.From("session-seam"),
        AgentId = AgentId.From("agent-seam"),
        Prompt = "Which environment should I deploy to?",
        InputType = AskUserInputType.SingleChoice,
        Choices =
        [
            new AskUserChoice { Value = "staging", Label = "Staging", Description = "safe" },
            new AskUserChoice { Value = "prod", Label = "Production" }
        ]
    };

    [Fact]
    public async Task Simulated_non_signalr_channel_can_submit_free_form_text()
    {
        using var registry = new AskUserResponseRegistry();
        var resolver = CreateResolver(registry);
        var (requestId, pending) = registry.Register(Conversation, TimeSpan.FromMinutes(1));

        var channel = new SimulatedPromptChannel(resolver, supportsInteractivePrompts: true);
        var result = await channel.AnswerWithTextAsync(Conversation, requestId, "staging please");

        result.Succeeded.ShouldBeTrue(result.FailureReason);
        var response = await pending;
        response.RequestId.ShouldBe(requestId);
        response.FreeFormText.ShouldBe("staging please");
        response.SelectedValues.ShouldBeNull();
        response.WasCancelled.ShouldBeFalse();
    }

    [Fact]
    public async Task Simulated_non_signalr_channel_can_submit_structured_selection()
    {
        using var registry = new AskUserResponseRegistry();
        var resolver = CreateResolver(registry);
        var (requestId, pending) = registry.Register(Conversation, TimeSpan.FromMinutes(1));

        var channel = new SimulatedPromptChannel(resolver, supportsInteractivePrompts: true);
        var result = await channel.AnswerWithSelectionAsync(Conversation, requestId, ["prod"]);

        result.Succeeded.ShouldBeTrue(result.FailureReason);
        var response = await pending;
        response.SelectedValues.ShouldNotBeNull();
        response.SelectedValues!.ShouldHaveSingleItem().ShouldBe("prod");
        response.FreeFormText.ShouldBeNull();
    }

    [Fact]
    public async Task Simulated_non_signalr_channel_can_submit_explicit_cancel()
    {
        using var registry = new AskUserResponseRegistry();
        var resolver = CreateResolver(registry);
        var (requestId, pending) = registry.Register(Conversation, TimeSpan.FromMinutes(1));

        var channel = new SimulatedPromptChannel(resolver, supportsInteractivePrompts: true);
        var result = await channel.CancelAsync(Conversation, requestId);

        result.Succeeded.ShouldBeTrue(result.FailureReason);
        var response = await pending;
        response.WasCancelled.ShouldBeTrue();
        response.FreeFormText.ShouldBeNull();
        response.SelectedValues.ShouldBeNull();
    }

    [Fact]
    public async Task Adapter_without_interactive_prompt_capability_still_gets_a_usable_text_prompt()
    {
        using var registry = new AskUserResponseRegistry();
        var resolver = CreateResolver(registry);
        var (requestId, pending) = registry.Register(Conversation, TimeSpan.FromMinutes(1));

        var channel = new SimulatedPromptChannel(resolver, supportsInteractivePrompts: false);
        channel.SupportsInteractivePrompts.ShouldBeFalse();

        var rendered = channel.Render(CreateRequest(requestId).ToPrompt());

        // The degraded rendering must still convey the question, every option, and how to answer.
        rendered.ShouldContain("Which environment should I deploy to?");
        rendered.ShouldContain("1. Staging");
        rendered.ShouldContain("2. Production");
        rendered.ShouldContain("Reply with the number of your choice");

        // ...and a reply expressed against that rendering must resolve to the structured value.
        var result = await channel.AnswerTextDegradedAsync(CreateRequest(requestId).ToPrompt(), "2");

        result.Succeeded.ShouldBeTrue(result.FailureReason);
        var response = await pending;
        response.SelectedValues.ShouldNotBeNull();
        response.SelectedValues!.ShouldHaveSingleItem().ShouldBe("prod");
    }

    [Fact]
    public async Task Resolution_is_rejected_when_no_prompt_is_pending()
    {
        using var registry = new AskUserResponseRegistry();
        var resolver = CreateResolver(registry);

        var result = await resolver.ResolveAsync(new AskUserSubmission
        {
            ConversationId = Conversation,
            FreeFormText = "hello"
        });

        result.Succeeded.ShouldBeFalse();
        result.Status.ShouldBe(AskUserResolutionStatus.NoPendingPrompt);
    }

    [Fact]
    public async Task Resolution_is_rejected_when_the_request_id_targets_a_superseded_prompt()
    {
        using var registry = new AskUserResponseRegistry();
        var resolver = CreateResolver(registry);
        _ = registry.Register(Conversation, TimeSpan.FromMinutes(1));

        var result = await resolver.ResolveAsync(new AskUserSubmission
        {
            ConversationId = Conversation,
            RequestId = "a-stale-request-id",
            FreeFormText = "late button press"
        });

        result.Succeeded.ShouldBeFalse();
        result.Status.ShouldBe(AskUserResolutionStatus.NoPendingPrompt);
    }

    [Fact]
    public async Task Resolution_is_rejected_when_the_submission_says_nothing()
    {
        using var registry = new AskUserResponseRegistry();
        var resolver = CreateResolver(registry);
        _ = registry.Register(Conversation, TimeSpan.FromMinutes(1));

        var result = await resolver.ResolveAsync(new AskUserSubmission
        {
            ConversationId = Conversation,
            FreeFormText = "   ",
            SelectedValues = ["  "]
        });

        result.Succeeded.ShouldBeFalse();
        result.Status.ShouldBe(AskUserResolutionStatus.InvalidSubmission);
    }

    [Fact]
    public async Task Submission_without_a_request_id_targets_the_pending_prompt()
    {
        using var registry = new AskUserResponseRegistry();
        var resolver = CreateResolver(registry);
        var (requestId, pending) = registry.Register(Conversation, TimeSpan.FromMinutes(1));

        var result = await resolver.ResolveAsync(new AskUserSubmission
        {
            ConversationId = Conversation,
            FreeFormText = "an inbound text reply carries no request id"
        });

        result.Succeeded.ShouldBeTrue(result.FailureReason);
        result.RequestId.ShouldBe(requestId);
        (await pending).RequestId.ShouldBe(requestId);
    }

    /// <summary>
    /// A stand-in for a future non-SignalR channel adapter (Telegram, Discord, TUI). It holds
    /// nothing but the shared resolver, proving a channel needs no registry access, no bespoke
    /// entry point, and no Blazor client reference to participate in <c>ask_user</c>.
    /// </summary>
    private sealed class SimulatedPromptChannel(IAskUserPromptResolver resolver, bool supportsInteractivePrompts)
        : ChannelAdapterBase(NullLogger<SimulatedPromptChannel>.Instance)
    {
        public override ChannelKey ChannelType => ChannelKey.From("simulated");

        public override string DisplayName => "Simulated Channel";

        public override bool SupportsInteractivePrompts => supportsInteractivePrompts;

        public string Render(AskUserPrompt prompt)
            => SupportsInteractivePrompts
                ? prompt.Prompt
                : AskUserPromptTextRenderer.Render(prompt);

        public ValueTask<AskUserResolutionResult> AnswerWithTextAsync(
            ConversationId conversationId,
            string requestId,
            string text)
            => resolver.ResolveAsync(new AskUserSubmission
            {
                ConversationId = conversationId,
                RequestId = requestId,
                FreeFormText = text,
                OriginChannel = ChannelType
            });

        public ValueTask<AskUserResolutionResult> AnswerWithSelectionAsync(
            ConversationId conversationId,
            string requestId,
            IReadOnlyList<string> values)
            => resolver.ResolveAsync(new AskUserSubmission
            {
                ConversationId = conversationId,
                RequestId = requestId,
                SelectedValues = values,
                OriginChannel = ChannelType
            });

        public ValueTask<AskUserResolutionResult> CancelAsync(ConversationId conversationId, string requestId)
            => resolver.ResolveAsync(new AskUserSubmission
            {
                ConversationId = conversationId,
                RequestId = requestId,
                Cancelled = true,
                OriginChannel = ChannelType
            });

        /// <summary>
        /// The inbound half of the text-degraded fallback: a plain reply is mapped back onto a
        /// structured choice when it matches one, and forwarded as free text otherwise.
        /// </summary>
        public ValueTask<AskUserResolutionResult> AnswerTextDegradedAsync(AskUserPrompt prompt, string reply)
        {
            var matched = AskUserPromptTextRenderer.MatchChoice(prompt, reply);
            return resolver.ResolveAsync(new AskUserSubmission
            {
                ConversationId = ConversationId.From(prompt.ConversationId),
                RequestId = prompt.RequestId,
                SelectedValues = matched is null ? null : [matched],
                FreeFormText = matched is null ? reply : null,
                OriginChannel = ChannelType
            });
        }

        protected override Task OnStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        protected override Task OnStopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
