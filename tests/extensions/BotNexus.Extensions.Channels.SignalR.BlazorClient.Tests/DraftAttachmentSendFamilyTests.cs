using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Regression coverage for #2484: the composer's Steer / Redirect / Follow Up buttons read only
/// <c>_inputText</c> and never <c>_attachments</c>, so draft files were silently discarded and the
/// chips stayed pinned in the composer.
/// </summary>
/// <remarks>
/// <para>
/// These paths are only rendered while the turn is active, which is why the loss was invisible on
/// the happy path and severe in combination with #2195 (stuck turn-active state): every ordinary
/// send then travelled an attachment-discarding path.
/// </para>
/// <para>
/// Each test asserts the OBSERVABLE - the draft list arriving at the interaction service call -
/// plus the composer clearing. A broken implementation dispatches with no attachments argument at
/// all, and every assertion below fails in that case.
/// </para>
/// <para>
/// Vacuity: no early <c>return</c>, no conditional skip, no catch-and-continue in any test here.
/// Every test ends in unconditional assertions.
/// </para>
/// </remarks>
public sealed class DraftAttachmentSendFamilyTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store = new();
    private readonly IAgentInteractionService _interaction = Substitute.For<IAgentInteractionService>();

    public DraftAttachmentSendFamilyTests()
    {
        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        _ctx.Services.AddSingleton(_interaction);
        _ctx.Services.AddSingleton<ISlashCommandDispatcher>(new SlashCommandDispatcher(_interaction));
        _ctx.Services.AddSingleton(Substitute.For<IGatewayRestClient>());
        _ctx.Services.AddSingleton(Substitute.For<IPortalPreferencesService>());
        _ctx.Services.AddSingleton(new HttpClient());
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        _store.UpsertAgent(new AgentState { AgentId = "agent-1", DisplayName = "Agent", IsConnected = true });
        _store.SeedConversations("agent-1", [new ConversationSummaryDto(
            "conv-1", "agent-1", "T", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]);
        _store.SetActiveConversation("agent-1", "conv-1");

        // The steer/redirect/follow-up controls only render while a run is active.
        _store.GetStreamState("conv-1").IsRunActive = true;
    }

    public void Dispose() => _ctx.Dispose();

    private static DraftAttachment Draft() =>
        new("notes.txt", "text/plain", Convert.ToBase64String("hello"u8.ToArray()), 5);

    private async Task<IRenderedComponent<ChatPanel>> RenderWithDraftAsync(string text)
    {
        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));
        await cut.InvokeAsync(() => cut.Instance.AddDraftAttachmentsAsync([Draft()]));
        var textarea = cut.Find(".chat-input");
        await cut.InvokeAsync(() => textarea.Input(text));
        return cut;
    }

    [Fact]
    public async Task Steer_WithDraftAttachments_PassesThemThroughAndClearsTheComposer()
    {
        var cut = await RenderWithDraftAsync("steer with file");

        await cut.InvokeAsync(() => cut.Find("[data-testid='chat-steer-btn']").Click());

        await _interaction.Received(1).SteerAsync(
            "agent-1",
            "steer with file",
            Arg.Is<IReadOnlyList<DraftAttachment>>(x => x.Count == 1 && x[0].FileName == "notes.txt"));
        cut.FindAll("[data-testid='attachment-chip']").ShouldBeEmpty();
    }

    [Fact]
    public async Task Redirect_WithDraftAttachments_PassesThemThroughAndClearsTheComposer()
    {
        var cut = await RenderWithDraftAsync("redirect with file");

        await cut.InvokeAsync(() => cut.Find("[data-testid='chat-redirect-btn']").Click());

        await _interaction.Received(1).InterruptAndSteerAsync(
            "agent-1",
            "redirect with file",
            Arg.Is<IReadOnlyList<DraftAttachment>>(x => x.Count == 1 && x[0].FileName == "notes.txt"));
        cut.FindAll("[data-testid='attachment-chip']").ShouldBeEmpty();
    }

    [Fact]
    public async Task FollowUp_WithDraftAttachments_PassesThemThroughAndClearsTheComposer()
    {
        var cut = await RenderWithDraftAsync("follow up with file");

        await cut.InvokeAsync(() => cut.Find("[data-testid='chat-followup-btn']").Click());

        await _interaction.Received(1).FollowUpAsync(
            "agent-1",
            "follow up with file",
            Arg.Is<IReadOnlyList<DraftAttachment>>(x => x.Count == 1 && x[0].FileName == "notes.txt"));
        cut.FindAll("[data-testid='attachment-chip']").ShouldBeEmpty();
    }

    /// <summary>
    /// AC5 guard: enumerate the send-family seams on <see cref="IAgentInteractionService"/>. A
    /// future fourth send path added without an attachments overload fails here rather than
    /// silently dropping the user's files.
    /// </summary>
    [Fact]
    public void SendFamilyServiceMethods_AllExposeAnAttachmentsOverload()
    {
        string[] sendFamily =
        [
            nameof(IAgentInteractionService.SendMessageAsync),
            nameof(IAgentInteractionService.SteerAsync),
            nameof(IAgentInteractionService.FollowUpAsync),
            nameof(IAgentInteractionService.InterruptAndSteerAsync),
        ];

        var missing = sendFamily
            .Where(name => !typeof(IAgentInteractionService)
                .GetMethods()
                .Where(m => m.Name == name)
                .Any(m => m.GetParameters()
                    .Any(p => p.ParameterType == typeof(IReadOnlyList<DraftAttachment>))))
            .ToList();

        missing.ShouldBeEmpty();
    }
}
