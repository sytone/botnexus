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
/// Issue #2325 stage 1: the conversation section list and assignment map moved out of
/// <c>ConversationSectionsPanel</c> into the scoped <see cref="ConversationSectionsState"/> service.
/// These tests pin that the sidebar "move to section" menu resolves its sections and its current
/// assignment from that service - not from a component <c>@ref</c>. Under the previous design the
/// menu silently emptied whenever the panel was not in the render tree; here the state is authored
/// entirely through the service, with no panel interaction at all.
/// </summary>
public sealed class ConversationSectionsStateLiftTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store = new();
    private readonly SectionsStubHandler _handler = new();
    private readonly ConversationSectionsState _state;

    public ConversationSectionsStateLiftTests()
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

    private IRenderedComponent<MainLayout> RenderLayout()
    {
        _store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        _store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-1", "a-1", "Planning", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        _store.SelectView("a-1", string.Empty, SelectionSource.UserClick);

        return _ctx.Render<MainLayout>(p => p.Add(c => c.Body, (RenderFragment)(_ => { })));
    }

    private void SeedSections(string sectionsJson, string assignmentsJson = "{}") =>
        _handler.SetJson("GET", "/api/agents/a-1/sections",
            $$"""{"sections":{{sectionsJson}},"assignments":{{assignmentsJson}}}""");

    [Fact]
    public async Task AssignMenu_Resolves_Sections_From_The_Lifted_State()
    {
        SeedSections("""[{"sectionId":"sec_1","agentId":"a-1","name":"Work","order":0,"isCollapsed":false}]""");

        var cut = RenderLayout();
        cut.WaitForAssertion(() => cut.Find("[data-testid='conversation-section-btn']").Click());

        cut.WaitForAssertion(() =>
        {
            var items = cut.FindAll("[data-testid='conversation-section-menu-item']");
            Assert.Single(items);
            Assert.Equal("Work", items[0].TextContent.Trim());
        });

        // Author new section data purely through the lifted service - no panel interaction at all.
        // With state owned by the component this menu could not have seen the change.
        SeedSections("""[{"sectionId":"sec_1","agentId":"a-1","name":"Work","order":0,"isCollapsed":false},{"sectionId":"sec_2","agentId":"a-1","name":"Home","order":1,"isCollapsed":false}]""");
        await cut.InvokeAsync(() => _state.ReloadAsync());

        cut.WaitForAssertion(() =>
        {
            var ids = cut.FindAll("[data-testid='conversation-section-menu-item']")
                .Select(e => e.GetAttribute("data-section-id"))
                .ToList();
            Assert.Equal(["sec_1", "sec_2"], ids);
        });
    }

    [Fact]
    public void AssignMenu_Marks_The_Current_Section_From_The_Lifted_Assignments()
    {
        SeedSections(
            """[{"sectionId":"sec_1","agentId":"a-1","name":"Work","order":0,"isCollapsed":false}]""",
            """{"c-1":"sec_1"}""");

        var cut = RenderLayout();
        cut.WaitForAssertion(() => cut.Find("[data-testid='conversation-section-btn']").Click());

        cut.WaitForAssertion(() =>
        {
            // "active" comes from ConversationSectionsState.GetAssignedSectionId - the load-bearing lookup.
            Assert.Contains("active", cut.Find("[data-testid='conversation-section-menu-item']").GetAttribute("class"));
            Assert.DoesNotContain("active", cut.Find("[data-testid='conversation-section-menu-none']").GetAttribute("class"));
        });
    }

    [Fact]
    public async Task State_Service_Exposes_Sections_And_Assignments_Without_Any_Component()
    {
        SeedSections(
            """[{"sectionId":"sec_2","agentId":"a-1","name":"Home","order":1,"isCollapsed":false},{"sectionId":"sec_1","agentId":"a-1","name":"Work","order":0,"isCollapsed":false}]""",
            """{"c-1":"sec_1"}""");

        await _state.EnsureLoadedAsync("a-1");

        Assert.Equal(["sec_1", "sec_2"], _state.Sections.Select(s => s.SectionId));
        Assert.Equal("sec_1", _state.GetAssignedSectionId("c-1"));
        Assert.Null(_state.GetAssignedSectionId("c-unknown"));
        Assert.Equal(["c-1"], _state.ConversationsFor("sec_1"));
    }

    private sealed record RecordedRequest(string Method, string Path);

    private sealed class SectionsStubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string? Body)> _responses = new(StringComparer.Ordinal);

        public List<RecordedRequest> Requests { get; } = [];

        public void SetJson(string method, string path, string body, HttpStatusCode status = HttpStatusCode.OK)
            => _responses[$"{method} {path}"] = (status, body);

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

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            });
        }
    }
}
