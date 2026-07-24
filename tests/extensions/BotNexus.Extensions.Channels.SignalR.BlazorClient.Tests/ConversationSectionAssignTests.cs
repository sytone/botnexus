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
/// Regression coverage for issue #2324: user-defined conversation sections (#2124) shipped with a
/// full management panel but no way to put a conversation *into* a section -
/// <c>SectionsApiClient.AssignAsync</c> had zero UI call sites, so every section was permanently
/// empty. These tests assert the sidebar row exposes a "move to section" affordance and that using
/// it actually issues the assign / unassign calls against the gateway.
/// </summary>
public sealed class ConversationSectionAssignTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store = new();
    private readonly SectionsStubHandler _handler = new();

    public ConversationSectionAssignTests()
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
        _ctx.Services.AddSingleton(new SectionsApiClient(http));
        _ctx.Services.AddSingleton(new ToolsApiClient(http));
        _ctx.Services.AddStubNavOrderApiClient();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private IRenderedComponent<MainLayout> RenderLayout()
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-1", "a-1", "Planning", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        _store.SelectView("a-1", string.Empty, SelectionSource.UserClick);

        return _ctx.Render<MainLayout>(p => p.Add(c => c.Body, (RenderFragment)(_ => { })));
    }

    private void SeedSections(string assignmentsJson = "{}") =>
        _handler.SetJson("GET", "/api/agents/a-1/sections",
            $$"""{"sections":[{"sectionId":"sec_1","agentId":"a-1","name":"Work","order":0,"isCollapsed":false}],"assignments":{{assignmentsJson}}}""");

    [Fact]
    public void ConversationRow_Exposes_MoveToSection_Affordance()
    {
        SeedSections();

        var cut = RenderLayout();

        // The affordance must exist on a non-default conversation row - this is the gap #2324 reports.
        cut.WaitForAssertion(() =>
            Assert.NotNull(cut.Find("[data-testid='conversation-section-btn']")));
    }

    [Fact]
    public void SectionMenu_Lists_Agent_Sections_And_None()
    {
        SeedSections();

        var cut = RenderLayout();
        cut.WaitForAssertion(() => cut.Find("[data-testid='conversation-section-btn']").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(cut.Find("[data-testid='conversation-section-menu-none']"));
            var items = cut.FindAll("[data-testid='conversation-section-menu-item']");
            Assert.Single(items);
            Assert.Equal("Work", items[0].TextContent.Trim());
        });
    }

    [Fact]
    public void Choosing_A_Section_Assigns_The_Conversation_On_The_Server()
    {
        SeedSections();
        _handler.SetStatus("PUT", "/api/agents/a-1/sections/sec_1/conversations/c-1", HttpStatusCode.NoContent);

        var cut = RenderLayout();
        cut.WaitForAssertion(() => cut.Find("[data-testid='conversation-section-btn']").Click());
        cut.WaitForAssertion(() => cut.Find("[data-testid='conversation-section-menu-item']").Click());

        // AssignAsync now has a real UI call site (acceptance criterion of #2324).
        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests,
            r => r.Method == "PUT" && r.Path == "/api/agents/a-1/sections/sec_1/conversations/c-1"));
    }

    [Fact]
    public void Choosing_None_Unassigns_The_Conversation_On_The_Server()
    {
        // Conversation starts assigned to sec_1 so "None" is a real unassign, not a no-op.
        SeedSections("""{"c-1":"sec_1"}""");
        _handler.SetStatus("DELETE", "/api/agents/a-1/sections/conversations/c-1", HttpStatusCode.NoContent);

        var cut = RenderLayout();
        cut.WaitForAssertion(() => cut.Find("[data-testid='conversation-section-btn']").Click());
        cut.WaitForAssertion(() => cut.Find("[data-testid='conversation-section-menu-none']").Click());

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests,
            r => r.Method == "DELETE" && r.Path == "/api/agents/a-1/sections/conversations/c-1"));
    }

    [Fact]
    public void Assigning_Updates_The_Rendered_Section_Grouping()
    {
        SeedSections();
        _handler.SetStatus("PUT", "/api/agents/a-1/sections/sec_1/conversations/c-1", HttpStatusCode.NoContent);

        var cut = RenderLayout();
        cut.WaitForAssertion(() => cut.Find("[data-testid='conversation-section-btn']").Click());

        // After the assign round-trips, the panel reloads and the server now reports the assignment,
        // so the conversation renders inside the section body instead of the empty-state text.
        SeedSections("""{"c-1":"sec_1"}""");
        cut.WaitForAssertion(() => cut.Find("[data-testid='conversation-section-menu-item']").Click());

        cut.WaitForAssertion(() =>
        {
            var item = cut.Find("[data-testid='section-conversation-item']");
            Assert.Equal("c-1", item.GetAttribute("data-conversation-id"));
            Assert.Contains("Planning", item.TextContent);
        });
    }

    private sealed record RecordedRequest(string Method, string Path);

    private sealed class SectionsStubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string? Body)> _responses = new(StringComparer.Ordinal);

        public List<RecordedRequest> Requests { get; } = [];

        public void SetJson(string method, string path, string body, HttpStatusCode status = HttpStatusCode.OK)
            => _responses[$"{method} {path}"] = (status, body);

        public void SetStatus(string method, string path, HttpStatusCode status)
            => _responses[$"{method} {path}"] = (status, null);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var method = request.Method.Method;
            lock (Requests)
                Requests.Add(new RecordedRequest(method, path));

            if (_responses.TryGetValue($"{method} {path}", out var configured))
            {
                var msg = new HttpResponseMessage(configured.Status);
                if (configured.Body is not null)
                    msg.Content = new StringContent(configured.Body, Encoding.UTF8, "application/json");
                return Task.FromResult(msg);
            }

            // Unconfigured calls (cron list, tools, ...) return an empty JSON array/object shape that
            // the portal's clients tolerate, so nothing in the layout throws during these tests.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            });
        }
    }
}
