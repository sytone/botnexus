using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class PromptTemplatePickerTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store = new();
    private readonly IAgentInteractionService _interaction = Substitute.For<IAgentInteractionService>();
    private readonly IGatewayRestClient _rest = Substitute.For<IGatewayRestClient>();

    public PromptTemplatePickerTests()
    {
        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        _ctx.Services.AddSingleton(_interaction);
        _ctx.Services.AddSingleton(_rest);
        _ctx.Services.AddSingleton<ISlashCommandDispatcher>(new SlashCommandDispatcher(_interaction));
        var preferences = Substitute.For<IPortalPreferencesService>();
        preferences.Current.Returns(new PortalPreferences());
        _ctx.Services.AddSingleton(preferences);
        _ctx.Services.AddSingleton(new HttpClient());
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _store.UpsertAgent(new AgentState { AgentId = "agent-1", DisplayName = "Agent", IsConnected = true });
        _store.SeedConversations("agent-1", [Conversation("conv-1"), Conversation("conv-2")]);
        _store.SetActiveConversation("agent-1", "conv-1");
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Picker_shows_loading_empty_and_error_states()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<PromptTemplateDescriptorDto>>();
        _rest.GetPromptTemplatesAsync("agent-1", Arg.Any<CancellationToken>()).Returns(pending.Task);
        var cut = Render();
        cut.Find("[data-testid='prompt-template-open']").Click();
        cut.Find("[data-testid='prompt-template-loading']");
        pending.SetResult([]);
        cut.WaitForAssertion(() => cut.Find("[data-testid='prompt-template-empty']"));

        _rest.GetPromptTemplatesAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<PromptTemplateDescriptorDto>>>(_ => throw new HttpRequestException("secret server detail"));
        cut.Find("[data-testid='prompt-template-close']").Click();
        cut.Find("[data-testid='prompt-template-open']").Click();
        cut.WaitForAssertion(() =>
        {
            var alert = cut.Find("[data-testid='prompt-template-error']");
            alert.TextContent.ShouldContain("couldn’t load");
            alert.TextContent.ShouldNotContain("secret server detail");
        });
    }

    [Fact]
    public void Search_filters_and_keyboard_selects_a_template()
    {
        _rest.GetPromptTemplatesAsync("agent-1", Arg.Any<CancellationToken>()).Returns(Templates());
        var cut = Render();
        cut.Find("[data-testid='prompt-template-open']").Click();
        cut.WaitForAssertion(() => cut.FindAll("[role='option']").Count.ShouldBe(2));
        cut.Find("[data-testid='prompt-template-search']").Input("review");
        cut.FindAll("[role='option']").Count.ShouldBe(1);
        cut.Find("[data-testid='prompt-template-search']").KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });
        cut.Find("[data-testid='prompt-template-search']").KeyDown(new KeyboardEventArgs { Key = "Enter" });
        cut.Find("[data-testid='prompt-template-parameters']");
        cut.Markup.ShouldContain("Pull request number");
    }

    [Fact]
    public async Task Parameter_entry_renders_exact_preview()
    {
        _rest.GetPromptTemplatesAsync("agent-1", Arg.Any<CancellationToken>()).Returns(Templates());
        _rest.RenderPromptTemplateAsync("agent-1", "review", Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(call => new PromptTemplateRenderResultDto($"Review PR {call.ArgAt<IReadOnlyDictionary<string, string>>(2)["pr"]}\r\nOK", new Dictionary<string, string[]>()));
        var cut = Render();
        SelectReview(cut);
        cut.Find("[data-testid='prompt-template-parameter-pr']").Input("42");
        cut.Find("[data-testid='prompt-template-preview-button']").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid='prompt-template-preview']").TextContent.ShouldBe("Review PR 42\nOK"));
        await _rest.Received(1).RenderPromptTemplateAsync(
            "agent-1", "review", Arg.Is<IReadOnlyDictionary<string, string>>(x => x["pr"] == "42"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Out_of_order_preview_cannot_repopulate_text_after_parameter_change()
    {
        _rest.GetPromptTemplatesAsync("agent-1", Arg.Any<CancellationToken>()).Returns(Templates());
        var pending = new TaskCompletionSource<PromptTemplateRenderResultDto>();
        _rest.RenderPromptTemplateAsync("agent-1", "review", Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>()).Returns(pending.Task);
        var cut = Render();
        SelectReview(cut);
        cut.Find("[data-testid='prompt-template-parameter-pr']").Input("1");
        cut.Find("[data-testid='prompt-template-preview-button']").Click();
        cut.Find("[data-testid='prompt-template-parameter-pr']").Input("2");
        pending.SetResult(new PromptTemplateRenderResultDto("stale preview", new Dictionary<string, string[]>(), null));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='prompt-template-preview']").ShouldBeEmpty());
    }

    [Fact]
    public void General_render_error_is_visible()
    {
        _rest.GetPromptTemplatesAsync("agent-1", Arg.Any<CancellationToken>()).Returns(Templates());
        _rest.RenderPromptTemplateAsync("agent-1", "review", Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new PromptTemplateRenderResultDto(null, new Dictionary<string, string[]>(), "The template could not be rendered."));
        var cut = Render();
        SelectReview(cut);
        cut.Find("[data-testid='prompt-template-preview-button']").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid='prompt-template-render-error']").TextContent.ShouldBe("The template could not be rendered."));
    }

    [Fact]
    public async Task Insert_replaces_selection_preserving_prefix_suffix_unicode_and_line_endings_without_dispatch()
    {
        _rest.GetPromptTemplatesAsync("agent-1", Arg.Any<CancellationToken>()).Returns(Templates());
        _rest.RenderPromptTemplateAsync("agent-1", "review", Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new PromptTemplateRenderResultDto("middle\r\n\u4e2d\ud83d\ude42", new Dictionary<string, string[]>()));
        _ctx.JSInterop.Setup<PromptTemplateInsertionResult>("chatComposer.replaceSelection", _ => true)
            .SetResult(new PromptTemplateInsertionResult("prefix middle\r\n\u4e2d\ud83d\ude42 suffix", 21, 21));
        var cut = Render();
        cut.Find("[data-testid='chat-input']").Input("prefix SELECT suffix");
        SelectReview(cut);
        cut.Find("[data-testid='prompt-template-parameter-pr']").Input("42");
        cut.Find("[data-testid='prompt-template-preview-button']").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid='prompt-template-insert']").Click());
        // HTML textarea values normalize CRLF to LF when projected through the DOM. The exact
        // JavaScript helper test separately proves the insertion operation preserves the supplied
        // CRLF bytes before that standards-defined DOM projection.
        cut.WaitForAssertion(() => cut.Find("[data-testid='chat-input']").GetAttribute("value").ShouldBe("prefix middle\n\u4e2d\ud83d\ude42 suffix"));
        await _interaction.DidNotReceiveWithAnyArgs().DeliverMessageAsync(default!, default!, default!, default, default!);
    }

    [Fact]
    public async Task Insert_at_caret_keeps_attachments_and_delivery_intent_unchanged()
    {
        _rest.GetPromptTemplatesAsync("agent-1", Arg.Any<CancellationToken>()).Returns(Templates());
        _rest.RenderPromptTemplateAsync("agent-1", "review", Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new PromptTemplateRenderResultDto("inserted", new Dictionary<string, string[]>()));
        _ctx.JSInterop.Setup<PromptTemplateInsertionResult>("chatComposer.replaceSelection", _ => true)
            .SetResult(new PromptTemplateInsertionResult("beforeinsertedafter", 14, 14));
        var cut = Render();
        await cut.InvokeAsync(() => cut.Instance.AddDraftAttachmentsAsync([
            new DraftAttachment("note.txt", "text/plain", "bm90ZQ==", 4)]));
        SelectReview(cut);
        cut.Find("[data-testid='prompt-template-parameter-pr']").Input("7");
        cut.Find("[data-testid='prompt-template-preview-button']").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid='prompt-template-insert']").Click());
        cut.FindAll("[data-testid='attachment-chip']").Count.ShouldBe(1);
        await _interaction.DidNotReceiveWithAnyArgs().DeliverMessageAsync(default!, default!, default!, default, default!);
    }

    [Fact]
    public async Task Conversation_switch_invalidates_stale_insertion_context()
    {
        _rest.GetPromptTemplatesAsync("agent-1", Arg.Any<CancellationToken>()).Returns(Templates());
        _rest.RenderPromptTemplateAsync("agent-1", "review", Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new PromptTemplateRenderResultDto("stale", new Dictionary<string, string[]>()));
        var cut = Render("conv-1");
        SelectReview(cut);
        cut.Find("[data-testid='prompt-template-parameter-pr']").Input("1");
        cut.Find("[data-testid='prompt-template-preview-button']").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid='prompt-template-preview']"));
        cut.Render(p => p.Add(c => c.AgentId, "agent-1").Add(c => c.ConversationId, "conv-2"));
        cut.FindAll("[data-testid='prompt-template-dialog']").ShouldBeEmpty();
        _ctx.JSInterop.Invocations.ShouldNotContain(x => x.Identifier == "chatComposer.replaceSelection");
        await _interaction.DidNotReceiveWithAnyArgs().DeliverMessageAsync(default!, default!, default!, default, default!);
    }

    [Fact]
    public void Modal_exposes_stable_active_option_and_focus_lifecycle()
    {
        _rest.GetPromptTemplatesAsync("agent-1", Arg.Any<CancellationToken>()).Returns(Templates());
        var cut = Render();
        cut.Find("[data-testid='prompt-template-open']").Click();
        cut.WaitForAssertion(() => cut.FindAll("[role='option']").Count.ShouldBe(2));
        cut.Find("[data-testid='prompt-template-search']").GetAttribute("aria-activedescendant").ShouldBe("prompt-template-option-0");
        cut.FindAll("[role='option']").Select(option => option.Id).ShouldBe(["prompt-template-option-0", "prompt-template-option-1"]);
        cut.Find("[data-testid='prompt-template-search']").Input("review");
        cut.Find("[data-testid='prompt-template-search']").GetAttribute("aria-activedescendant").ShouldBe("prompt-template-option-1");
        _ctx.JSInterop.Invocations.ShouldContain(invocation => invocation.Identifier == "chatComposer.focusFirst");
        cut.Find("[data-testid='prompt-template-close']").Click();
        cut.WaitForAssertion(() => _ctx.JSInterop.Invocations.ShouldContain(invocation => invocation.Identifier == "chatComposer.focusElement"));
    }

    private IRenderedComponent<ChatPanel> Render(string conversationId = "conv-1") =>
        _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1").Add(c => c.ConversationId, conversationId));

    private static ConversationSummaryDto Conversation(string id) => new(
        id, "agent-1", id, false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static IReadOnlyList<PromptTemplateDescriptorDto> Templates() =>
    [
        new("standup", "Daily standup", "shared", []),
        new("review", "Review a pull request", "workspace",
            [new PromptTemplateParameterDto("pr", "Pull request number", null, true)])
    ];

    private static void SelectReview(IRenderedComponent<ChatPanel> cut)
    {
        cut.Find("[data-testid='prompt-template-open']").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid='prompt-template-search']"));
        cut.Find("[data-testid='prompt-template-search']").Input("review");
        cut.Find("[role='option']").Click();
    }
}
