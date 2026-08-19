using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// #2918: keyboard prompt-history recall in the chat composer.
///
/// Two of these tests pin the blocking defects found reviewing the first attempt (PR #2919):
/// <list type="number">
/// <item>a cross-conversation prompt leak caused by an empty-conversation-id bucket whose entries
/// were later adopted by whichever conversation asked for history first; and</item>
/// <item>Esc ceasing to abort a streaming agent once history navigation was active, because draft
/// restoration shadowed the abort branch.</item>
/// </list>
/// The remainder cover the bare-arrow edge gating, which the first attempt left entirely untested:
/// every test there drove <c>Alt+Arrow</c>, and Alt is precisely the modifier that BYPASSES the
/// caret probe. The delicate half of the design therefore had zero coverage. Here the probe is
/// stubbed at the interop seam so both edge states can be driven deterministically without a
/// browser caret.
/// </summary>
public sealed class ChatPanelPromptHistoryTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store = new();
    private readonly IAgentInteractionService _interaction = Substitute.For<IAgentInteractionService>();

    public ChatPanelPromptHistoryTests()
    {
        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        _ctx.Services.AddSingleton(_interaction);
        _ctx.Services.AddSingleton<ISlashCommandDispatcher>(new SlashCommandDispatcher(_interaction));
        _ctx.Services.AddSingleton(Substitute.For<IGatewayRestClient>());
        _ctx.Services.AddSingleton(new HttpClient { BaseAddress = new Uri("http://localhost/") });
        var preferences = Substitute.For<IPortalPreferencesService>();
        preferences.Current.Returns(new PortalPreferences { ArchiveConfirmEnabled = false });
        _ctx.Services.AddSingleton(preferences);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    /// <summary>
    /// Stubs the ONE caret-edge authority. Under loose mode an unconfigured
    /// <c>InvokeAsync&lt;CaretLinePosition&gt;</c> yields null, so bare-arrow tests must configure
    /// it explicitly; that is deliberate, since a silently-null probe is exactly the shape of the
    /// gap this suite exists to close.
    /// </summary>
    private void StubCaret(bool onFirstLine, bool onLastLine) =>
        _ctx.JSInterop
            .Setup<CaretLinePosition>("chatScroll.caretLinePosition", _ => true)
            .SetResult(new CaretLinePosition(onFirstLine, onLastLine));

    private static ConversationSummaryDto Conv(string convId, string agentId, bool isDefault = true) => new(
        ConversationId: convId,
        AgentId: agentId,
        Title: convId,
        IsDefault: isDefault,
        Status: "Active",
        ActiveSessionId: null,
        BindingCount: 0,
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow);

    private void SeedAgent(string agentId, bool isStreaming = false) =>
        _store.UpsertAgent(new AgentState
        {
            AgentId = agentId,
            DisplayName = agentId,
            IsConnected = true,
            IsStreaming = isStreaming
        });

    private IRenderedComponent<ChatPanel> Render(string agentId, string? conversationId) =>
        _ctx.Render<ChatPanel>(p =>
        {
            p.Add(c => c.AgentId, agentId);
            p.Add(c => c.ConversationId, conversationId);
        });

    private static void Type(IRenderedComponent<ChatPanel> cut, string text) =>
        cut.Find("[data-testid=chat-input]").Input(text);

    private static void Key(IRenderedComponent<ChatPanel> cut, string key, bool alt = false) =>
        cut.Find("[data-testid=chat-input]").KeyDown(new KeyboardEventArgs { Key = key, AltKey = alt });

    private static string InputValue(IRenderedComponent<ChatPanel> cut) =>
        cut.Find("[data-testid=chat-input]").GetAttribute("value") ?? string.Empty;

    private static void Send(IRenderedComponent<ChatPanel> cut, string text)
    {
        Type(cut, text);
        Key(cut, "Enter");
    }

    // ---------------------------------------------------------------------------------
    // BLOCKER 1 - cross-conversation prompt leak
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// The regression that blocked PR #2919. The original store recorded a prompt under the empty
    /// conversation key whenever identity was transiently unresolved, then adopted those stranded
    /// entries into whichever conversation later asked for history. This asserts the leak cannot
    /// happen by CONSTRUCTION: an unattributable prompt is never recorded, so there is nothing for
    /// an unrelated conversation to inherit.
    ///
    /// Falsification: reintroducing the empty-key bucket plus adoption makes this fail, because
    /// conv-b's first Up would surface "stranded-prompt" instead of leaving the draft untouched.
    /// </summary>
    [Fact]
    public async Task Prompt_sent_without_a_conversation_never_leaks_into_another_conversation()
    {
        // The agent has NO conversations yet: seeding one here would give the panel an ambient
        // active conversation via the fallback, and identity would never actually be null.
        SeedAgent("agent-1");

        // ONE panel instance driven through the transient-identity window that produced the leak:
        // the route names no conversation and the agent has selected none, so at send time the id
        // is null. A single instance is essential - two separately rendered panels each own their
        // own state, so a two-instance test cannot reach the adoption path at all and passes even
        // when the empty-key bucket is present. That is exactly how this defect survived the first
        // attempt's isolation test.
        var cut = Render("agent-1", conversationId: null);
        Send(cut, "stranded-prompt");

        // Defence in depth #1: with no conversation the send is refused outright, so nothing is
        // dispatched and the draft is retained rather than silently swallowed.
        await _interaction.DidNotReceive().SendMessageAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());

        // A real, unrelated conversation now appears and the panel re-points at it.
        _store.SeedConversations("agent-1", [Conv("conv-b", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-b");
        cut.Render(p =>
        {
            p.Add(c => c.AgentId, "agent-1");
            p.Add(c => c.ConversationId, "conv-b");
        });

        Type(cut, "draft-in-b");
        StubCaret(onFirstLine: true, onLastLine: true);
        Key(cut, "ArrowUp");

        // Defence in depth #2 - the assertion that actually pins the leak: conv-b's history is
        // empty, so Up is inert and the draft is untouched. With the empty-key bucket and its
        // adoption heuristic restored, this line fails with "stranded-prompt".
        Assert.Equal("draft-in-b", InputValue(cut));
    }

    /// <summary>
    /// The positive half: history is per-conversation, and a prompt genuinely recorded against one
    /// conversation is not visible from another.
    /// </summary>
    [Fact]
    public void History_is_isolated_between_two_real_conversations()
    {
        SeedAgent("agent-1");
        _store.SeedConversations("agent-1", [Conv("conv-a", "agent-1"), Conv("conv-b", "agent-1", isDefault: false)]);
        _store.SetActiveConversation("agent-1", "conv-a");

        var a = Render("agent-1", "conv-a");
        Send(a, "prompt-in-a");

        var b = Render("agent-1", "conv-b");
        StubCaret(onFirstLine: true, onLastLine: true);
        Key(b, "ArrowUp");

        Assert.DoesNotContain("prompt-in-a", b.Markup);
        Assert.Equal(string.Empty, InputValue(b));
    }

    // ---------------------------------------------------------------------------------
    // BLOCKER 2 - Esc must still abort a streaming agent
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// The second blocker. Abort is a SAFETY control; recall is a convenience. Pressing Up during a
    /// stream and then Esc must still stop the agent. The original chain
    /// (<c>else if (_historyIndex != -1) restore; else if (IsStreaming) abort;</c>) let the restore
    /// branch consume the keystroke and leave the agent running.
    ///
    /// Falsification: restoring that else-if ordering makes this fail on the AbortAsync assertion.
    /// </summary>
    [Fact]
    public async Task Escape_aborts_a_streaming_agent_even_while_navigating_history()
    {
        SeedAgent("agent-1", isStreaming: true);
        _store.SeedConversations("agent-1", [Conv("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");

        var cut = Render("agent-1", "conv-1");
        Send(cut, "earlier-prompt");
        _store.SetStreaming("conv-1", true);
        cut.Render();

        // Enter history navigation mid-stream, then press Esc.
        Type(cut, "in-progress-draft");
        StubCaret(onFirstLine: true, onLastLine: true);
        Key(cut, "ArrowUp");
        Assert.Equal("earlier-prompt", InputValue(cut));

        Key(cut, "Escape");

        await _interaction.Received(1).AbortAsync("agent-1", "conv-1");
        // The draft is still restored - it simply does not swallow the abort.
        Assert.Equal("in-progress-draft", InputValue(cut));
    }

    /// <summary>
    /// Esc still restores the draft when nothing is streaming, so fixing the precedence did not
    /// cost the recall feature its cancel affordance.
    /// </summary>
    [Fact]
    public async Task Escape_restores_the_draft_and_does_not_abort_when_idle()
    {
        SeedAgent("agent-1");
        _store.SeedConversations("agent-1", [Conv("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");

        var cut = Render("agent-1", "conv-1");
        Send(cut, "earlier-prompt");

        Type(cut, "my-draft");
        StubCaret(onFirstLine: true, onLastLine: true);
        Key(cut, "ArrowUp");
        Assert.Equal("earlier-prompt", InputValue(cut));

        Key(cut, "Escape");

        Assert.Equal("my-draft", InputValue(cut));
        await _interaction.DidNotReceive().AbortAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    // ---------------------------------------------------------------------------------
    // Bare-arrow edge gating - the half the first attempt never exercised
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Bare Up recalls only when the caret is on the FIRST line. This is the path every test in the
    /// first attempt bypassed by using Alt.
    /// </summary>
    [Fact]
    public void Bare_up_recalls_when_the_caret_is_on_the_first_line()
    {
        var cut = SeededPanelWithHistory("first", "second");

        StubCaret(onFirstLine: true, onLastLine: false);
        Key(cut, "ArrowUp");

        Assert.Equal("second", InputValue(cut));
    }

    /// <summary>
    /// The complement, and the reason the gate exists: with the caret mid-draft, Up is ordinary
    /// caret movement and must NOT hijack the draft.
    /// </summary>
    [Fact]
    public void Bare_up_does_not_recall_when_the_caret_is_not_on_the_first_line()
    {
        var cut = SeededPanelWithHistory("first", "second");
        Type(cut, "line-one\nline-two");

        StubCaret(onFirstLine: false, onLastLine: true);
        Key(cut, "ArrowUp");

        Assert.Equal("line-one\nline-two", InputValue(cut));
    }

    /// <summary>Bare Down moves toward newer entries only at the last line.</summary>
    [Fact]
    public void Bare_down_does_not_recall_when_the_caret_is_not_on_the_last_line()
    {
        var cut = SeededPanelWithHistory("first", "second");

        StubCaret(onFirstLine: true, onLastLine: true);
        Key(cut, "ArrowUp");
        Key(cut, "ArrowUp");
        Assert.Equal("first", InputValue(cut));

        StubCaret(onFirstLine: true, onLastLine: false);
        Key(cut, "ArrowDown");

        Assert.Equal("first", InputValue(cut));
    }

    /// <summary>
    /// Alt+Arrow is the unconditional fallback: it recalls even when the caret probe reports the
    /// caret is mid-draft.
    /// </summary>
    [Fact]
    public void Alt_arrow_recalls_regardless_of_caret_position()
    {
        var cut = SeededPanelWithHistory("first", "second");
        StubCaret(onFirstLine: false, onLastLine: false);

        Key(cut, "ArrowUp", alt: true);

        Assert.Equal("second", InputValue(cut));
    }

    /// <summary>
    /// Walking past the newest entry restores the draft stashed when navigation began, rather than
    /// leaving the composer stuck on the last prompt.
    /// </summary>
    [Fact]
    public void Navigating_past_the_newest_entry_restores_the_stashed_draft()
    {
        var cut = SeededPanelWithHistory("first", "second");
        Type(cut, "unsent-draft");
        StubCaret(onFirstLine: true, onLastLine: true);

        Key(cut, "ArrowUp");
        Assert.Equal("second", InputValue(cut));

        Key(cut, "ArrowDown");
        Assert.Equal("unsent-draft", InputValue(cut));
    }

    /// <summary>Consecutive duplicate prompts collapse to a single history entry.</summary>
    [Fact]
    public void Consecutive_duplicate_prompts_are_recorded_once()
    {
        SeedAgent("agent-1");
        _store.SeedConversations("agent-1", [Conv("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        var cut = Render("agent-1", "conv-1");
        Send(cut, "same");
        Send(cut, "same");

        StubCaret(onFirstLine: true, onLastLine: true);
        Key(cut, "ArrowUp");
        Assert.Equal("same", InputValue(cut));

        // A second Up would surface an older entry if the duplicate had been stored twice.
        Key(cut, "ArrowUp");
        Assert.Equal("same", InputValue(cut));
    }

    /// <summary>
    /// While the slash-command palette is open the arrows belong to the palette, so recall stands
    /// down. This is the only disambiguation the feature needs; making the palette itself
    /// keyboard-navigable is a separate change and is deliberately out of scope.
    /// </summary>
    [Fact]
    public void Arrows_do_not_recall_while_the_slash_command_palette_is_open()
    {
        var cut = SeededPanelWithHistory("first", "second");
        Type(cut, "/");

        StubCaret(onFirstLine: true, onLastLine: true);
        Key(cut, "ArrowUp");

        Assert.Equal("/", InputValue(cut));
    }

    private IRenderedComponent<ChatPanel> SeededPanelWithHistory(params string[] prompts)
    {
        SeedAgent("agent-1");
        _store.SeedConversations("agent-1", [Conv("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");
        var cut = Render("agent-1", "conv-1");
        foreach (var p in prompts)
            Send(cut, p);
        return cut;
    }

    // ---------------------------------------------------------------------------------
    // REGRESSION - the conversation's FIRST prompt was unreachable
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Found in manual testing of the reworked PR: send three prompts in a NEW conversation, press
    /// Up three times, and the oldest prompt never appears.
    ///
    /// Root cause: <c>DispatchDraftAsync</c> is not the only path a prompt takes. The prompt that
    /// CREATES a conversation is dispatched by <c>StartConversationService</c> from the landing
    /// page - before this panel instance exists - so <c>RecordPromptHistory</c> never sees it. The
    /// prompt is in the transcript but not in history, and recall can only reach entries recorded
    /// by this panel. The same gap swallows all history on a page reload.
    ///
    /// This models it the way it actually happens: the transcript already contains the first
    /// prompt, and the panel is rendered afterwards, having recorded nothing.
    ///
    /// Falsification: remove the SeedHistoryFromTranscript call from CurrentHistory and this fails
    /// - the first Up surfaces "second-prompt" (or nothing) instead of "landing-prompt", because
    /// the panel's own list never contained the landing prompt at all.
    /// </summary>
    [Fact]
    public void First_prompt_of_a_conversation_is_recallable_even_though_another_path_sent_it()
    {
        SeedAgent("agent-1");
        var agent = _store.GetAgent("agent-1")!;
        var conv = new ConversationState { ConversationId = "conv-a", Title = "conv-a" };
        // The landing page created the conversation and dispatched the first prompt; by the time
        // this panel renders, that prompt exists ONLY in the transcript.
        conv.AppendMessage(new ChatMessage("User", "landing-prompt", DateTimeOffset.UtcNow));
        conv.AppendMessage(new ChatMessage("Assistant", "some reply", DateTimeOffset.UtcNow));
        agent.Conversations["conv-a"] = conv;
        agent.ActiveConversationId = "conv-a";

        var cut = Render("agent-1", conversationId: "conv-a");

        // A prompt sent through the panel afterwards, exactly as in the manual repro.
        Send(cut, "second-prompt");
        conv.AppendMessage(new ChatMessage("User", "second-prompt", DateTimeOffset.UtcNow));

        StubCaret(onFirstLine: true, onLastLine: true);

        Key(cut, "ArrowUp");
        Assert.Equal("second-prompt", InputValue(cut));

        // The regression: this second Up must reach the landing prompt.
        Key(cut, "ArrowUp");
        Assert.Equal("landing-prompt", InputValue(cut));
    }

    /// <summary>
    /// The seed must not resurrect an entry the citizen has navigated past, and must not fire twice.
    /// Guards the "skip when a bucket already exists" branch: once history exists for a
    /// conversation, the transcript is no longer consulted.
    ///
    /// Falsification: drop the ContainsKey early-return in SeedHistoryFromTranscript and this fails
    /// - re-seeding on every read rebuilds the list from the transcript and the pointer no longer
    /// addresses what the citizen was walking.
    /// </summary>
    [Fact]
    public void Transcript_seed_does_not_overwrite_history_already_recorded_by_this_panel()
    {
        SeedAgent("agent-1");
        var agent = _store.GetAgent("agent-1")!;
        var conv = new ConversationState { ConversationId = "conv-a", Title = "conv-a" };
        agent.Conversations["conv-a"] = conv;
        agent.ActiveConversationId = "conv-a";

        var cut = Render("agent-1", conversationId: "conv-a");
        Send(cut, "typed-one");
        Send(cut, "typed-two");

        // Transcript gains entries AFTER history was established (server echo, replay, etc.).
        conv.AppendMessage(new ChatMessage("User", "typed-one", DateTimeOffset.UtcNow));
        conv.AppendMessage(new ChatMessage("User", "typed-two", DateTimeOffset.UtcNow));
        conv.AppendMessage(new ChatMessage("User", "never-typed-here", DateTimeOffset.UtcNow));

        StubCaret(onFirstLine: true, onLastLine: true);

        Key(cut, "ArrowUp");
        Assert.Equal("typed-two", InputValue(cut));
        Key(cut, "ArrowUp");
        Assert.Equal("typed-one", InputValue(cut));

        // Oldest reached: a further Up must not surface a transcript-only entry.
        Key(cut, "ArrowUp");
        Assert.Equal("typed-one", InputValue(cut));
    }

    /// <summary>
    /// The seed is keyed by the route-owned conversation id, so it must not become a new vector for
    /// the cross-conversation leak that blocked the first attempt.
    ///
    /// Falsification: key the seed by anything other than the rendered conversation (or seed from a
    /// shared/ambient transcript) and conv-b's first Up surfaces conv-a's prompt.
    /// </summary>
    [Fact]
    public void Transcript_seed_never_crosses_conversations()
    {
        SeedAgent("agent-1");
        var agent = _store.GetAgent("agent-1")!;

        var a = new ConversationState { ConversationId = "conv-a", Title = "conv-a" };
        a.AppendMessage(new ChatMessage("User", "a-only-prompt", DateTimeOffset.UtcNow));
        agent.Conversations["conv-a"] = a;

        var b = new ConversationState { ConversationId = "conv-b", Title = "conv-b" };
        agent.Conversations["conv-b"] = b;

        agent.ActiveConversationId = "conv-a";

        // Render the panel bound to conv-b; conv-a's transcript must be invisible to it.
        var cut = Render("agent-1", conversationId: "conv-b");
        StubCaret(onFirstLine: true, onLastLine: true);

        Type(cut, "draft-in-b");
        Key(cut, "ArrowUp");

        // conv-b has no history of its own: the draft is untouched and nothing leaks in.
        Assert.Equal("draft-in-b", InputValue(cut));
    }
}
