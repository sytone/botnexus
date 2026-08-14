using System.Net;
using System.Text;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Layout;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Issue #2325 stage 2. Two defects, both user-visible:
/// <list type="number">
/// <item>a section member was a bare <c>&lt;span&gt;</c> with no href and no click handler, so an
/// assigned conversation could not be opened at all - the panel was decoration;</item>
/// <item>section assignment was not subtracted from the default list, so an assigned conversation
/// rendered twice.</item>
/// </list>
/// The navigation test asserts on the resulting <see cref="NavigationManager.Uri"/>, not on markup:
/// a class or a <c>data-testid</c> assertion would have passed against the broken non-interactive
/// span, which is exactly the vacuity Jon called out.
/// </summary>
public sealed class ConversationSectionNavigationTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store = new();
    private readonly SectionsStubHandler _handler = new();
    private readonly ConversationSectionsState _state;

    public ConversationSectionNavigationTests()
    {
        var http = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost/") };
        var restClient = Substitute.For<IGatewayRestClient>();
        restClient.ApiBaseUrl.Returns("");
        var portalLoad = Substitute.For<IPortalLoadService>();
        portalLoad.IsReady.Returns(false);
        portalLoad.IsLoading.Returns(true);
        portalLoad.LoadError.Returns((string?)null);
        var prefs = Substitute.For<IPortalPreferencesService>();
        prefs.Current.Returns(new PortalPreferences());
        var sectionsApi = new SectionsApiClient(http);
        _state = new ConversationSectionsState(sectionsApi);
        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        _ctx.Services.AddSingleton(Substitute.For<IAgentInteractionService>());
        _ctx.Services.AddSingleton<ISlashCommandDispatcher>(sp => new SlashCommandDispatcher(sp.GetRequiredService<IAgentInteractionService>()));
        _ctx.Services.AddSingleton(portalLoad);
        _ctx.Services.AddSingleton(new GatewayHubConnection());
        _ctx.Services.AddSingleton(new GatewayInfoService(http, restClient));
        _ctx.Services.AddSingleton(Substitute.For<IUpdateStatusService>());
        _ctx.Services.AddSingleton(prefs);
        _ctx.Services.AddSingleton(restClient);
        _ctx.Services.AddSingleton(Substitute.For<IChannelErrorReporter>());
        _ctx.Services.AddSingleton(http);
        _ctx.Services.AddSingleton(new ExtensionFeatureService(restClient));
        _ctx.Services.AddSingleton(new CronApiClient(http));
        _ctx.Services.AddSingleton(sectionsApi);
        _ctx.Services.AddSingleton(_state);
        _ctx.Services.AddSingleton(new ToolsApiClient(http));
        _ctx.Services.AddStubNavOrderApiClient();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private void SeedSections(string sectionsJson, string assignmentsJson = "{}") =>
        _handler.SetJson("GET", "/api/agents/a-1/sections",
            $$"""{"sections":{{sectionsJson}},"assignments":{{assignmentsJson}}}""");

    private IRenderedComponent<MainLayout> RenderLayout()
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-1", "a-1", "Planning", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ConversationSummaryDto("c-2", "a-1", "Loose", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        _store.SelectView("a-1", string.Empty, SelectionSource.UserClick);
        return _ctx.Render<MainLayout>(p => p.Add(c => c.Body, (RenderFragment)(_ => { })));
    }

    /// <summary>
    /// Expands the "My sections" child, which starts collapsed via a class rather than unmounting.
    /// </summary>
    private static void ExpandSections(IRenderedComponent<MainLayout> cut)
    {
        var body = cut.Find("[data-testid='chat-sections-body']");
        if (body.GetAttribute("class")?.Contains("collapsed", StringComparison.Ordinal) == true)
            cut.Find("[data-testid='sections-group-toggle']").Click();
    }

    /// <summary>
    /// LOAD-BEARING (#2325 non-vacuity). Clicking a section member must actually navigate to
    /// <c>agent/{agentId}/conversation/{conversationId}</c>. The assertion is on the resulting URI,
    /// so rendering the row as a non-interactive element fails this test: a span cannot be clicked
    /// into a navigation and the URI stays on the base address.
    /// </summary>
    [Fact]
    public void Clicking_A_Section_Member_Navigates_To_That_Conversation()
    {
        SeedSections(
            """[{"sectionId":"sec_1","agentId":"a-1","name":"Work","order":0,"isCollapsed":false}]""",
            """{"c-1":"sec_1"}""");
        var cut = RenderLayout();
        var nav = _ctx.Services.GetRequiredService<NavigationManager>();

        cut.WaitForAssertion(() =>
        {
            ExpandSections(cut);
            var member = cut.FindAll("[data-testid='section-conversation-item']")
                .Single(e => e.GetAttribute("data-conversation-id") == "c-1");
            // Click the row's title element itself. If it is a bare span this is inert and the
            // navigation assertion below never becomes true.
            member.QuerySelector(".conversation-section-item-title")!.Click();
        });

        cut.WaitForAssertion(() =>
            Assert.Equal("http://localhost/agent/a-1/conversation/c-1", nav.Uri));
    }

    /// <summary>
    /// LOAD-BEARING (#2325 duplicate rows). A section-assigned conversation must render in exactly
    /// one place across the whole sidebar. Counted by <c>data-conversation-id</c> over every rendered
    /// row - default list rows and section member rows alike - so a reappearance in either place
    /// fails. The unassigned sibling pins that this is a subtraction, not a blanket hide.
    /// </summary>
    [Fact]
    public void A_Section_Assigned_Conversation_Renders_Exactly_Once()
    {
        SeedSections(
            """[{"sectionId":"sec_1","agentId":"a-1","name":"Work","order":0,"isCollapsed":false}]""",
            """{"c-1":"sec_1"}""");
        var cut = RenderLayout();

        cut.WaitForAssertion(() =>
        {
            ExpandSections(cut);
            Assert.Single(cut.FindAll("[data-testid='section-conversation-item']"));
        });

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("[data-conversation-id]")
                .Select(e => e.GetAttribute("data-conversation-id"))
                .ToList();
            Assert.Equal(1, rows.Count(id => id == "c-1"));
            Assert.Equal(1, rows.Count(id => id == "c-2"));
        });
    }

    /// <summary>
    /// The subtraction must not be able to lose a conversation. An assignment pointing at a section
    /// that is not in the loaded section list (deleted server-side, or a load that has not landed)
    /// leaves the conversation in the default list rather than hiding it into nothing.
    /// </summary>
    [Fact]
    public void An_Assignment_To_An_Unknown_Section_Keeps_The_Conversation_In_The_Default_List()
    {
        SeedSections("[]", """{"c-1":"sec_gone"}""");
        var cut = RenderLayout();

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("[data-testid='conversation-list-item']")
                .Select(e => e.GetAttribute("data-conversation-id"))
                .ToList();
            Assert.Contains("c-1", rows);
        });
    }

    private sealed class SectionsStubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string? Body)> _responses = new(StringComparer.Ordinal);

        public void SetJson(string method, string path, string body, HttpStatusCode status = HttpStatusCode.OK)
            => _responses[$"{method} {path}"] = (status, body);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = $"{request.Method.Method} {request.RequestUri!.AbsolutePath}";
            if (_responses.TryGetValue(key, out var configured))
            {
                var msg = new HttpResponseMessage(configured.Status);
                if (configured.Body is not null)
                    msg.Content = new StringContent(configured.Body, Encoding.UTF8, "application/json");
                return Task.FromResult(msg);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            });
        }
    }
}
