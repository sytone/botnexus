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
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='conversation-actions-trigger']")));
        await cut.InvokeAsync(() => cut.Find("[data-testid='conversation-actions-trigger']")
            .TriggerEventAsync("onclick", new Microsoft.AspNetCore.Components.Web.MouseEventArgs()));

        cut.WaitForAssertion(() =>
        {
            var items = cut.FindAll("[data-action-id^='move-']:not([data-action-id='move-none'])");
            Assert.Single(items);
            Assert.Equal("Move to: Work", items[0].TextContent.Trim());
        });

        // Author new section data purely through the lifted service - no panel interaction at all.
        // With state owned by the component this menu could not have seen the change.
        SeedSections("""[{"sectionId":"sec_1","agentId":"a-1","name":"Work","order":0,"isCollapsed":false},{"sectionId":"sec_2","agentId":"a-1","name":"Home","order":1,"isCollapsed":false}]""");
        await cut.InvokeAsync(() => _state.ReloadAsync());

        // Loading removes move authorization and retires the old opening. A fresh opening must
        // resolve the updated lifted snapshot, rather than keep stale action callbacks alive.
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(["sec_1", "sec_2"], _state.Sections.Select(section => section.SectionId));
            Assert.True(_state.CanMutate);
            Assert.Empty(cut.FindAll("[data-testid='conversation-action-menu']"));
            Assert.Equal("false", cut.Find("[data-testid='conversation-actions-trigger']").GetAttribute("aria-expanded"));
        });
        await cut.InvokeAsync(() => cut.Find("[data-testid='conversation-actions-trigger']")
            .TriggerEventAsync("onclick", new Microsoft.AspNetCore.Components.Web.MouseEventArgs()));

        cut.WaitForAssertion(() =>
        {
            var ids = cut.FindAll("[data-action-id^='move-']:not([data-action-id='move-none'])")
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
        cut.WaitForAssertion(() => cut.Find("[data-testid='conversation-actions-trigger']").Click());

        cut.WaitForAssertion(() =>
        {
            // "active" comes from ConversationSectionsState.GetAssignedSectionId - the load-bearing lookup.
            Assert.Contains("(current)", cut.Find("[data-action-id='move-sec_1']").TextContent);
            Assert.DoesNotContain("(current)", cut.Find("[data-action-id='move-none']").TextContent);
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

    [Fact]
    public async Task Stale_In_Flight_Reload_Does_Not_Overwrite_A_Newer_Result()
    {
        // The first load is held open, so it RESOLVES AFTER the second one has already published.
        // Without generation versioning in ReloadAsync this is last-write-wins and the older,
        // single-section payload wins - stranding the UI on stale sections. This is the same
        // interleaving a user hits by assigning a section while the initial load is still running.
        SeedSections("""[{"sectionId":"sec_1","agentId":"a-1","name":"Work","order":0,"isCollapsed":false}]""");
        var gate = _handler.HoldNext("GET", "/api/agents/a-1/sections");

        var stale = _state.EnsureLoadedAsync("a-1");

        SeedSections("""[{"sectionId":"sec_1","agentId":"a-1","name":"Work","order":0,"isCollapsed":false},{"sectionId":"sec_2","agentId":"a-1","name":"Home","order":1,"isCollapsed":false}]""");
        await _state.ReloadAsync();
        Assert.Equal(["sec_1", "sec_2"], _state.Sections.Select(s => s.SectionId));

        gate.SetResult();
        await stale;

        Assert.Equal(["sec_1", "sec_2"], _state.Sections.Select(s => s.SectionId));
    }

    [Fact]
    public async Task AgentSwitch_InvalidatesSynchronously_AndOldCompletionCannotRepaint()
    {
        SeedSections("""[{"sectionId":"s","agentId":"a-1","name":"A"}]""");
        await _state.EnsureLoadedAsync("a-1");
        _handler.SetJson("GET", "/api/agents/b/sections", """{"sections":[],"assignments":{}}""");
        var gate = _handler.HoldNext("GET", "/api/agents/b/sections");
        var loading = _state.EnsureLoadedAsync("b");
        Assert.Equal("b", _state.AgentId);
        Assert.Empty(_state.Sections);
        Assert.Empty(_state.Assignments);
        Assert.False(_state.HasSnapshot);
        Assert.True(_state.IsLoading);
        await _state.EnsureLoadedAsync("a-1");
        gate.SetResult();
        await loading;
        Assert.Equal("a-1", _state.AgentId);
        Assert.Single(_state.Sections);
        Assert.True(_state.HasSnapshot);
    }

    [Fact]
    public async Task SameAgentFailure_RetryWorks_AndEmptySuccessIsDistinct()
    {
        _handler.SetJson("GET", "/api/agents/a-1/sections", "{}", HttpStatusCode.ServiceUnavailable);
        await _state.EnsureLoadedAsync("a-1");
        Assert.False(_state.HasSnapshot);
        Assert.NotNull(_state.LoadError);
        Assert.False(_state.IsLoading);
        SeedSections("[]");
        await _state.EnsureLoadedAsync("a-1");
        Assert.True(_state.HasSnapshot);
        Assert.Null(_state.LoadError);
        Assert.Empty(_state.Sections);
        Assert.Equal(4, _state.GroupOrder.Count);
    }

    [Fact]
    public async Task FailedReload_PreservesConfirmedSnapshot_AndExplicitRetryClearsError()
    {
        SeedSections("""[{"sectionId":"s","agentId":"a-1"}]""");
        await _state.EnsureLoadedAsync("a-1");
        _handler.SetJson("GET", "/api/agents/a-1/sections", "{}", HttpStatusCode.ServiceUnavailable);
        await _state.ReloadAsync();
        Assert.Single(_state.Sections);
        Assert.True(_state.HasSnapshot);
        Assert.NotNull(_state.LoadError);
        Assert.False(_state.CanMutate);
        SeedSections("[]");
        await _state.RetryAsync();
        Assert.Empty(_state.Sections);
        Assert.Null(_state.LoadError);
        Assert.True(_state.CanMutate);
    }

    [Fact]
    public async Task MixedSnapshot_ReconcilesWithoutRepairWrites_RequiresExplicitRefresh()
    {
        _handler.SetJson("GET", "/api/agents/a-1/sections", """{"sections":[{"sectionId":"s","agentId":"a-1"},{"sectionId":"foreign","agentId":"b"}],"assignments":{"c":"deleted"},"groupOrder":[{"system":"Webhooks"},{"sectionId":"deleted"}]}""");
        await _state.EnsureLoadedAsync("a-1");
        Assert.True(_state.RequiresReconciliation);
        Assert.False(_state.CanMutate);
        Assert.Null(_state.GetAssignedSectionId("c"));
        Assert.Single(_state.Sections);
        Assert.Equal(5, _state.GroupOrder.Count);
        Assert.All(_handler.Requests, r => Assert.Equal("GET", r.Method));
        SeedSections("[]");
        await _state.RetryAsync();
        Assert.False(_state.RequiresReconciliation);
        Assert.True(_state.CanMutate);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    public async Task UnifiedMove_FailurePreservesOrder_AndUnsupportedIsExplicit(HttpStatusCode status)
    {
        SeedSections("[]");
        await _state.EnsureLoadedAsync("a-1");
        var before = _state.GroupOrder.ToArray();
        _handler.SetJson("PUT", "/api/agents/a-1/sections/group-order", "{}", status);
        Assert.False(await _state.MoveGroupAsync(0, 3));
        Assert.Equal(before, _state.GroupOrder);
        Assert.NotNull(_state.MutationError);
        Assert.Equal(status != HttpStatusCode.BadRequest, _state.GroupOrderUnsupported);
        Assert.False(_state.IsSaving);
    }

    [Fact]
    public async Task CreateSavedRefreshFailed_IsNotReposted_UntilRefreshSucceeds()
    {
        SeedSections("[]");
        await _state.EnsureLoadedAsync("a-1");
        _handler.SetJson("POST", "/api/agents/a-1/sections", """{"sectionId":"new","agentId":"a-1"}""", HttpStatusCode.Created);
        _handler.SetJson("GET", "/api/agents/a-1/sections", "{}", HttpStatusCode.ServiceUnavailable);
        Assert.False(await _state.CreateAsync("Work"));
        Assert.True(_state.SavedButRefreshFailed);
        Assert.True(_state.RequiresRefresh);
        Assert.NotNull(_state.MutationError);
        Assert.Empty(_state.Sections);
        Assert.False(await _state.CreateAsync("Work"));
        Assert.Single(_handler.Requests, r => r.Method == "POST");
        SeedSections("""[{"sectionId":"new","agentId":"a-1"}]""");
        await _state.RetryAsync();
        Assert.Single(_state.Sections);
        Assert.False(_state.SavedButRefreshFailed);
        Assert.True(_state.CanMutate);
        Assert.Single(_handler.Requests, r => r.Method == "POST");
    }

    [Fact]
    public async Task LegacyMoveAndCollapse_ArePessimistic_PreserveStateOnRejection()
    {
        SeedSections("""[{"sectionId":"s1","agentId":"a-1","order":0},{"sectionId":"s2","agentId":"a-1","order":1}]""");
        await _state.EnsureLoadedAsync("a-1");
        _handler.SetJson("PUT", "/api/agents/a-1/sections/order", "{}", HttpStatusCode.BadRequest);
        await _state.MoveAsync(0, 1);
        Assert.Equal(new[] { "s1", "s2" }, _state.Sections.Select(s => s.SectionId));
        _handler.SetJson("PATCH", "/api/agents/a-1/sections/s1", "{}", HttpStatusCode.BadRequest);
        await _state.ToggleCollapseAsync(_state.Sections[0]);
        Assert.False(_state.Sections[0].IsCollapsed);
        Assert.NotNull(_state.MutationError);
    }

    [Fact]
    public async Task PendingWrite_SerializesAndCapturedAgentDoesNotRefreshNewScope()
    {
        SeedSections("[]");
        await _state.EnsureLoadedAsync("a-1");
        _handler.SetJson("POST", "/api/agents/a-1/sections", """{"sectionId":"new","agentId":"a-1"}""", HttpStatusCode.Created);
        var gate = _handler.HoldNext("POST", "/api/agents/a-1/sections");
        var save = _state.CreateAsync("Work");
        Assert.True(_state.IsSaving);
        Assert.False(await _state.CreateAsync("Duplicate"));
        _handler.SetJson("GET", "/api/agents/b/sections", """{"sections":[],"assignments":{}}""");
        await _state.EnsureLoadedAsync("b");
        Assert.False(_state.CanMutate); // Global scoped writer still holds the gate.
        gate.SetResult();
        Assert.True(await save);
        Assert.Equal("b", _state.AgentId);
        Assert.Empty(_state.Sections);
        Assert.Single(_handler.Requests, r => r.Path == "/api/agents/a-1/sections" && r.Method == "GET");
        Assert.Single(_handler.Requests, r => r.Method == "POST");
        Assert.True(_state.CanMutate);
    }

    [Fact]
    public async Task UnifiedMove_SuccessPublishesCanonicalServerOrderIncludingZeroGroups()
    {
        SeedSections("""[{"sectionId":"s1","agentId":"a-1","order":0},{"sectionId":"s2","agentId":"a-1","order":1}]""");
        await _state.EnsureLoadedAsync("a-1");
        _handler.SetJson("PUT", "/api/agents/a-1/sections/group-order", """[{"sectionId":"s2"},{"system":"Webhooks"},{"system":"Scheduled"},{"sectionId":"s1"},{"system":"Conversations"},{"system":"Pinned"}]""");
        Assert.True(await _state.MoveGroupAsync(0, 5));
        Assert.Equal("s2", _state.GroupOrder[0].SectionId);
        Assert.Equal(BotNexus.Gateway.Abstractions.Models.BuiltInConversationSection.Pinned, _state.GroupOrder[^1].System);
        Assert.Equal(new[] { "s2", "s1" }, _state.Sections.Select(s => s.SectionId));
        Assert.Equal(6, _state.GroupOrder.Count);
        Assert.Single(_handler.Requests, r => r.Method == "GET");
        Assert.Null(_state.MutationError);
    }

    [Fact]
    public async Task AToBToA_PendingOldWriteDoesNotRefreshNewAGeneration()
    {
        SeedSections("[]");
        await _state.EnsureLoadedAsync("a-1");
        _handler.SetJson("POST", "/api/agents/a-1/sections", """{"sectionId":"old","agentId":"a-1"}""", HttpStatusCode.Created);
        var gate = _handler.HoldNext("POST", "/api/agents/a-1/sections");
        var save = _state.CreateAsync("old");
        _handler.SetJson("GET", "/api/agents/b/sections", """{"sections":[],"assignments":{}}""");
        await _state.EnsureLoadedAsync("b");
        SeedSections("""[{"sectionId":"current","agentId":"a-1"}]""");
        await _state.EnsureLoadedAsync("a-1");
        gate.SetResult();
        Assert.True(await save);
        Assert.Equal("current", Assert.Single(_state.Sections).SectionId);
        Assert.Equal(2, _handler.Requests.Count(r => r.Method == "GET" && r.Path == "/api/agents/a-1/sections"));
    }

    [Fact]
    public async Task StaleReadFailure_DoesNotReplaceNewSuccessOrClearLoading()
    {
        SeedSections("[]");
        await _state.EnsureLoadedAsync("a-1");
        var gate = _handler.HoldNext("GET", "/api/agents/a-1/sections");
        var stale = _state.ReloadAsync();
        SeedSections("""[{"sectionId":"new","agentId":"a-1"}]""");
        await _state.ReloadAsync();
        _handler.SetJson("GET", "/api/agents/a-1/sections", "{}", HttpStatusCode.ServiceUnavailable);
        gate.SetResult();
        await stale;
        Assert.Equal("new", Assert.Single(_state.Sections).SectionId);
        Assert.Null(_state.LoadError);
        Assert.False(_state.IsLoading);
        Assert.True(_state.CanMutate);
    }

    [Fact]
    public async Task AssignmentRestrictions_RejectDefaultReadOnlyHiddenArchivedAndObserverWithoutWrites()
    {
        SeedSections("""[{"sectionId":"s","agentId":"a-1"}]""");
        await _state.EnsureLoadedAsync("a-1");
        var rows = new[]
        {
            new ConversationState { ConversationId = "default", IsDefault = true },
            new ConversationState { ConversationId = "readonly", Source = BotNexus.Gateway.Abstractions.Models.ConversationSource.Webhook },
            new ConversationState { ConversationId = "hidden", Visibility = BotNexus.Gateway.Abstractions.Models.ConversationVisibility.InternalHidden },
            new ConversationState { ConversationId = "archived", Status = "Archived" },
        };
        foreach (var row in rows)
        {
            Assert.False(await _state.AssignAsync(row, "s", SelectionSource.UserClick));
            Assert.False(await _state.UnassignAsync(row, SelectionSource.UserClick));
        }
        var writable = new ConversationState { ConversationId = "writable" };
        Assert.False(await _state.AssignAsync(writable, "s", SelectionSource.UserClick, agentIsReadOnly: true));
        Assert.False(await _state.AssignAsync(writable, "s", SelectionSource.SubAgentView));
        Assert.All(_handler.Requests, r => Assert.Equal("GET", r.Method));
    }

    [Fact]
    public async Task UnknownWriteOutcome_BlocksRepostAndInvalidatesEarlierRead_UntilExplicitRefresh()
    {
        SeedSections("[]");
        await _state.EnsureLoadedAsync("a-1");
        _handler.ThrowNext("POST", "/api/agents/a-1/sections");
        var writeGate = _handler.HoldNext("POST", "/api/agents/a-1/sections");
        var save = _state.CreateAsync("Work");
        var readGate = _handler.HoldNext("GET", "/api/agents/a-1/sections");
        var staleRead = _state.ReloadAsync();
        writeGate.SetResult();
        Assert.False(await save);
        Assert.True(_state.RequiresRefresh);
        Assert.False(_state.SavedButRefreshFailed);
        Assert.NotNull(_state.MutationError);
        readGate.SetResult();
        await staleRead;
        Assert.True(_state.RequiresRefresh);
        Assert.False(await _state.CreateAsync("Work"));
        Assert.Single(_handler.Requests, r => r.Method == "POST");
        await _state.RetryAsync();
        Assert.True(_state.CanMutate);
        Assert.Null(_state.MutationError);
        Assert.Single(_handler.Requests, r => r.Method == "POST");
    }

    [Fact]
    public async Task SavedMutation_InvalidatesPreCommitGet_AndNewestRefreshWins()
    {
        SeedSections("[]");
        await _state.EnsureLoadedAsync("a-1");
        _handler.SetJson("POST", "/api/agents/a-1/sections", """{"sectionId":"new","agentId":"a-1"}""", HttpStatusCode.Created);
        var writeGate = _handler.HoldNext("POST", "/api/agents/a-1/sections");
        var save = _state.CreateAsync("Work");
        var readGate = _handler.HoldNext("GET", "/api/agents/a-1/sections");
        var staleRead = _state.ReloadAsync();
        SeedSections("""[{"sectionId":"new","agentId":"a-1"}]""");
        writeGate.SetResult();
        Assert.True(await save);
        SeedSections("[]");
        readGate.SetResult();
        await staleRead;
        Assert.Equal("new", Assert.Single(_state.Sections).SectionId);
        Assert.True(_state.CanMutate);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{not-json")]
    public async Task ConfirmedUnreadableCreate_RequiresExplicitGet_BlocksRepostAndDiscardsEarlierRead(string body)
    {
        SeedSections("[]");
        await _state.EnsureLoadedAsync("a-1");
        _handler.SetJson("POST", "/api/agents/a-1/sections", body, HttpStatusCode.Created);
        var writeObserved = _handler.ObserveNext("POST", "/api/agents/a-1/sections");
        var writeGate = _handler.HoldNext("POST", "/api/agents/a-1/sections");
        var save = _state.CreateAsync(" Work ");
        await writeObserved.WaitAsync(TimeSpan.FromSeconds(10));
        var readGate = _handler.HoldNext("GET", "/api/agents/a-1/sections");
        var staleRead = _state.ReloadAsync();
        writeGate.SetResult();
        Assert.False(await save);
        Assert.True(_state.SavedButRefreshFailed);
        Assert.True(_state.RequiresRefresh);
        Assert.False(_state.CanMutate);
        Assert.False(_state.IsSaving);
        Assert.NotNull(_state.MutationError);
        Assert.Empty(_state.Sections);
        Assert.Equal(2, _handler.Requests.Count(r => r.Method == "GET")); // No automatic recovery GET.
        readGate.SetResult();
        await staleRead;
        Assert.True(_state.SavedButRefreshFailed);
        Assert.True(_state.RequiresRefresh);
        Assert.False(await _state.CreateAsync("Work"));
        Assert.Single(_handler.Requests, r => r.Method == "POST");

        SeedSections("""[{"sectionId":"new","agentId":"a-1","name":"Work"}]""");
        var recoveryGate = _handler.HoldNext("GET", "/api/agents/a-1/sections");
        var recovery = _state.RetryAsync();
        Assert.True(_state.IsLoading);
        Assert.False(_state.CanMutate);
        Assert.False(await _state.CreateAsync("Work"));
        recoveryGate.SetResult();
        await recovery;
        Assert.Equal("new", Assert.Single(_state.Sections).SectionId);
        Assert.False(_state.RequiresRefresh);
        Assert.False(_state.SavedButRefreshFailed);
        Assert.Null(_state.MutationError);
        Assert.True(_state.CanMutate);
        Assert.Equal(3, _handler.Requests.Count(r => r.Method == "GET"));
        Assert.Single(_handler.Requests, r => r.Method == "POST");
        AssertRequest(1, "POST", "/api/agents/a-1/sections", """{"name":"Work"}""");

        // Recovery unlocks an intentional new operation, not a replay of the create.
        _handler.SetJson("PATCH", "/api/agents/a-1/sections/new", "", HttpStatusCode.NoContent);
        SeedSections("""[{"sectionId":"new","agentId":"a-1","name":"Renamed"}]""");
        Assert.True(await _state.RenameAsync("new", "Renamed"));
        Assert.Equal("Renamed", Assert.Single(_state.Sections).Name);
        Assert.Single(_handler.Requests, r => r.Method == "POST");
    }

    [Fact]
    public async Task ConfirmedUnreadableOldCreate_AToBToA_DoesNotPoisonNewGeneration()
    {
        SeedSections("[]");
        await _state.EnsureLoadedAsync("a-1");
        _handler.SetJson("POST", "/api/agents/a-1/sections", "null", HttpStatusCode.Created);
        var writeObserved = _handler.ObserveNext("POST", "/api/agents/a-1/sections");
        var gate = _handler.HoldNext("POST", "/api/agents/a-1/sections");
        var save = _state.CreateAsync("Old draft");
        await writeObserved.WaitAsync(TimeSpan.FromSeconds(10));
        _handler.SetJson("GET", "/api/agents/b/sections", """{"sections":[],"assignments":{}}""");
        await _state.EnsureLoadedAsync("b");
        SeedSections("""[{"sectionId":"current","agentId":"a-1","name":"Current"}]""");
        await _state.EnsureLoadedAsync("a-1");
        gate.SetResult();
        Assert.False(await save);
        Assert.Equal("a-1", _state.AgentId);
        Assert.Equal("current", Assert.Single(_state.Sections).SectionId);
        Assert.False(_state.SavedButRefreshFailed);
        Assert.False(_state.RequiresRefresh);
        Assert.Null(_state.MutationError);
        Assert.True(_state.CanMutate);
        Assert.Equal(2, _handler.Requests.Count(r => r.Method == "GET" && r.Path == "/api/agents/a-1/sections"));
        Assert.Single(_handler.Requests, r => r.Method == "POST");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanonicalPut_MembershipMismatch_PerformsOneGetWithoutRepairWrites(bool mixedRefresh)
    {
        SeedSections("""[{"sectionId":"s1","agentId":"a-1","order":0},{"sectionId":"s2","agentId":"a-1","order":1}]""", """{"c":"s2"}""");
        await _state.EnsureLoadedAsync("a-1");
        var before = _state.GroupOrder.ToArray();
        _handler.SetJson("PUT", "/api/agents/a-1/sections/group-order", """[{"system":"Webhooks"},{"sectionId":"s1"},{"sectionId":"new"},{"system":"Pinned"},{"system":"Conversations"},{"system":"Scheduled"}]""");
        var writeObserved = _handler.ObserveNext("PUT", "/api/agents/a-1/sections/group-order");
        var writeGate = _handler.HoldNext("PUT", "/api/agents/a-1/sections/group-order");
        var save = _state.MoveGroupAsync(0, 5);
        await writeObserved.WaitAsync(TimeSpan.FromSeconds(10));
        _handler.SetJson("GET", "/api/agents/a-1/sections", mixedRefresh
            ? """{"sections":[{"sectionId":"s1","agentId":"a-1"},{"sectionId":"s3","agentId":"a-1","order":1},{"sectionId":"foreign","agentId":"b"}],"assignments":{"c":"s2","d":"s3"},"groupOrder":[{"sectionId":"s3"},{"system":"Webhooks"}]}"""
            : """{"sections":[{"sectionId":"s1","agentId":"a-1"},{"sectionId":"s3","agentId":"a-1","order":1}],"assignments":{"d":"s3"}}""");
        var observed = _handler.ObserveNext("GET", "/api/agents/a-1/sections");
        var readGate = _handler.HoldNext("GET", "/api/agents/a-1/sections");
        writeGate.SetResult();
        await observed.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(before, _state.GroupOrder);
        Assert.Equal("s2", _state.GetAssignedSectionId("c"));
        Assert.True(_state.IsSaving);
        Assert.True(_state.IsLoading);
        Assert.False(_state.CanMutate);
        Assert.False(await _state.MoveGroupAsync(0, 1));
        readGate.SetResult();
        Assert.Equal(!mixedRefresh, await save);
        Assert.Equal(new[] { "s1", "s3" }, _state.Sections.Select(s => s.SectionId).Order());
        Assert.Null(_state.GetAssignedSectionId("c"));
        Assert.Equal("s3", _state.GetAssignedSectionId("d"));
        Assert.Equal(6, _state.GroupOrder.Count);
        Assert.Equal(4, _state.GroupOrder.Count(g => g.System is not null));
        Assert.Equal(new[] { "s1", "s3" }, _state.GroupOrder.Select(g => g.SectionId).OfType<string>().Order());
        Assert.Equal(mixedRefresh, _state.RequiresReconciliation);
        Assert.Equal(mixedRefresh, _state.RequiresRefresh);
        Assert.Equal(mixedRefresh, _state.SavedButRefreshFailed);
        Assert.Equal(!mixedRefresh, _state.CanMutate);
        Assert.Equal(2, _handler.Requests.Count(r => r.Method == "GET"));
        Assert.Single(_handler.Requests, r => r.Method != "GET");
        AssertRequest(1, "PUT", "/api/agents/a-1/sections/group-order", """{"groupOrder":[{"system":"Conversations"},{"system":"Scheduled"},{"system":"Webhooks"},{"sectionId":"s1"},{"sectionId":"s2"},{"system":"Pinned"}]}""");
    }

    [Theory]
    [InlineData("rename", "PATCH", "/api/agents/a-1/sections/s1", "{\"name\":\"Renamed\"}")]
    [InlineData("delete", "DELETE", "/api/agents/a-1/sections/s1", null)]
    [InlineData("assign", "PUT", "/api/agents/a-1/sections/s2/conversations/c", null)]
    [InlineData("unassign", "DELETE", "/api/agents/a-1/sections/conversations/c", null)]
    [InlineData("collapse", "PATCH", "/api/agents/a-1/sections/s1", "{\"isCollapsed\":true}")]
    [InlineData("legacy-order", "PUT", "/api/agents/a-1/sections/order", "{\"sectionIds\":[\"s2\",\"s1\"]}")]
    public async Task SuccessfulMutation_UsesActualWireContract_AndPublishesOnlyConfirmedGet(string operation, string method, string path, string? payload)
    {
        SeedSections("""[{"sectionId":"s1","agentId":"a-1","name":"Original","order":0,"isCollapsed":false},{"sectionId":"s2","agentId":"a-1","name":"Second","order":1}]""", """{"c":"s1"}""");
        await _state.EnsureLoadedAsync("a-1");
        var row = new ConversationState { ConversationId = "c", IsPinned = true };
        var originalRow = row.Project(SelectionSource.UserClick);
        var beforeOrder = _state.GroupOrder.ToArray();
        _handler.SetJson(method, path, "", HttpStatusCode.NoContent);
        var writeObserved = _handler.ObserveNext(method, path);
        var writeGate = _handler.HoldNext(method, path);
        var save = ApplyMutationAsync(operation, row);
        await writeObserved.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(_state.IsSaving);
        Assert.False(_state.CanMutate);
        Assert.Equal("Original", _state.Sections[0].Name);
        Assert.False(_state.Sections[0].IsCollapsed);
        Assert.Equal("s1", _state.GetAssignedSectionId("c"));
        Assert.Equal(beforeOrder, _state.GroupOrder);
        AssertRequest(1, method, path, payload);

        var sections = operation switch
        {
            "delete" => """[{"sectionId":"s2","agentId":"a-1","name":"Second","order":0}]""",
            "rename" => """[{"sectionId":"s1","agentId":"a-1","name":"Renamed","order":0},{"sectionId":"s2","agentId":"a-1","name":"Second","order":1}]""",
            "collapse" => """[{"sectionId":"s1","agentId":"a-1","name":"Original","order":0,"isCollapsed":true},{"sectionId":"s2","agentId":"a-1","name":"Second","order":1}]""",
            "legacy-order" => """[{"sectionId":"s1","agentId":"a-1","name":"Original","order":1},{"sectionId":"s2","agentId":"a-1","name":"Second","order":0}]""",
            _ => """[{"sectionId":"s1","agentId":"a-1","name":"Original","order":0},{"sectionId":"s2","agentId":"a-1","name":"Second","order":1}]""",
        };
        var assignments = operation switch
        {
            "delete" or "unassign" => "{}",
            "assign" => """{"c":"s2"}""",
            _ => """{"c":"s1"}""",
        };
        SeedSections(sections, assignments);
        var readObserved = _handler.ObserveNext("GET", "/api/agents/a-1/sections");
        var readGate = _handler.HoldNext("GET", "/api/agents/a-1/sections");
        writeGate.SetResult();
        await readObserved.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(_state.IsLoading);
        Assert.True(_state.IsSaving);
        Assert.Equal(beforeOrder, _state.GroupOrder);
        Assert.Equal("Original", _state.Sections[0].Name);
        Assert.False(_state.Sections[0].IsCollapsed);
        Assert.Equal("s1", _state.GetAssignedSectionId("c"));
        readGate.SetResult();
        Assert.True(await save);
        Assert.True(_state.CanMutate);
        Assert.False(_state.IsSaving);
        Assert.False(_state.IsLoading);
        Assert.Null(_state.MutationError);
        Assert.False(_state.RequiresRefresh);
        Assert.False(_state.SavedButRefreshFailed);
        Assert.Equal(operation == "legacy-order" ? new[] { "s2", "s1" } : operation == "delete" ? new[] { "s2" } : new[] { "s1", "s2" }, _state.Sections.Select(s => s.SectionId));
        Assert.Equal(operation is "delete" or "unassign" ? null : operation == "assign" ? "s2" : "s1", _state.GetAssignedSectionId("c"));
        if (operation != "delete")
        {
            var first = _state.Sections.Single(s => s.SectionId == "s1");
            Assert.Equal(operation == "rename" ? "Renamed" : "Original", first.Name);
            Assert.Equal(operation == "collapse", first.IsCollapsed);
        }
        Assert.Equal(_state.Sections.Select(s => s.SectionId), _state.GroupOrder.Select(g => g.SectionId).OfType<string>());
        Assert.Equal(4, _state.GroupOrder.Count(g => g.System is not null));
        Assert.Equal(originalRow, row.Project(SelectionSource.UserClick));
        Assert.True(row.IsPinned);
        var groups = ConversationSidebarGroups.Project("a-1", [row], _state.AgentId, _state.Sections, _state.Assignments, _state.GroupOrder, SelectionSource.UserClick);
        var home = Assert.Single(groups, g => g.Conversations.Count > 0);
        Assert.Same(row, Assert.Single(home.Conversations));
        Assert.Equal(_state.GetAssignedSectionId("c"), home.Identity.SectionId);
        Assert.Equal(operation is "delete" or "unassign" ? BotNexus.Gateway.Abstractions.Models.BuiltInConversationSection.Pinned : (BotNexus.Gateway.Abstractions.Models.BuiltInConversationSection?)null, home.Identity.System);
        Assert.Equal(3, _handler.Requests.Count);
        AssertRequest(2, "GET", "/api/agents/a-1/sections", null);
    }

    private async Task<bool> ApplyMutationAsync(string operation, ConversationState row)
    {
        switch (operation)
        {
            case "rename": return await _state.RenameAsync("s1", " Renamed ");
            case "delete": return await _state.DeleteAsync("s1");
            case "assign": return await _state.AssignAsync(row, "s2", SelectionSource.UserClick);
            case "unassign": return await _state.UnassignAsync(row, SelectionSource.UserClick);
            case "collapse": await _state.ToggleCollapseAsync(_state.Sections[0]); return _state.MutationError is null;
            case "legacy-order": await _state.MoveAsync(0, 1); return _state.MutationError is null;
            default: throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private void AssertRequest(int index, string method, string path, string? payload)
    {
        Assert.Equal(method, _handler.Requests[index].Method);
        Assert.Equal(path, _handler.Requests[index].Path);
        var actual = _handler.RequestBodies[index];
        if (payload is null)
        {
            Assert.Null(actual);
            return;
        }
        var expectedNode = System.Text.Json.Nodes.JsonNode.Parse(payload);
        var actualNode = System.Text.Json.Nodes.JsonNode.Parse(actual ?? throw new InvalidOperationException("Missing request body"));
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(expectedNode, actualNode));
    }

    private sealed record RecordedRequest(string Method, string Path);

    private sealed class SectionsStubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string? Body)> _responses = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource> _gates = new(StringComparer.Ordinal);

        private readonly HashSet<string> _throws = new(StringComparer.Ordinal);

        private readonly Dictionary<string, TaskCompletionSource> _observations = new(StringComparer.Ordinal);
        public List<string?> RequestBodies { get; } = [];

        public Task ObserveNext(string method, string path)
        {
            var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _observations.Add($"{method} {path}", observed);
            return observed.Task;
        }

        public List<RecordedRequest> Requests { get; } = [];

        public void ThrowNext(string method, string path) => _throws.Add($"{method} {path}");

        public void SetJson(string method, string path, string body, HttpStatusCode status = HttpStatusCode.OK)
            => _responses[$"{method} {path}"] = (status, body);

        /// <summary>
        /// Gate that holds the NEXT matching response until the returned source is completed, so a
        /// test can force responses to land out of call order.
        /// </summary>
        public TaskCompletionSource HoldNext(string method, string path)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _gates[$"{method} {path}"] = gate;
            return gate;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var method = request.Method.Method;
            RequestBodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            lock (Requests)
                Requests.Add(new RecordedRequest(method, path));

            var key = $"{method} {path}";
            if (_gates.Remove(key, out var gate))
            {
                // Publish readiness only after taking the held response out of the fixture map.
                if (_observations.Remove(key, out var heldObserved))
                    heldObserved.SetResult();
                await gate.Task;
            }
            if (_observations.Remove(key, out var observed))
                observed.SetResult();

            if (_throws.Remove(key))
                throw new HttpRequestException("Simulated connection loss after request dispatch");

            if (_responses.TryGetValue(key, out var configured))
            {
                var msg = new HttpResponseMessage(configured.Status);
                if (configured.Body is not null)
                    msg.Content = new StringContent(configured.Body, Encoding.UTF8, "application/json");
                return msg;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            };
        }
    }
}
