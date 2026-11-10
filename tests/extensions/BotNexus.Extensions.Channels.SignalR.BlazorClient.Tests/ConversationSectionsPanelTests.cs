using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>Peer controls use confirmed state, full canonical ordering and actual rows, never raw assignment ids.</summary>
public sealed class ConversationSectionsPanelTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly StubHandler _handler = new();
    private readonly ConversationSectionsState _state;

    public ConversationSectionsPanelTests()
    {
        var http = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost/") };
        _state = new(new SectionsApiClient(http));
        _ctx.Services.AddSingleton(_state);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

    }

    public void Dispose() => _ctx.Dispose();

    private async Task<IRenderedComponent<ConversationSectionsPanel>> Render(bool controls = false)
    {
        await _state.EnsureLoadedAsync("a-1");
        return _ctx.Render<ConversationSectionsPanel>(p => p.Add(c => c.AgentId, "a-1")
            .Add(c => c.CreateOnly, controls).Add(c => c.Groups, Groups())
            .Add(c => c.RowContent, row => (RenderFragment)(builder => builder.AddContent(0, row.Title))));
    }

    private IReadOnlyList<ConversationSidebarGroup> Groups() => ConversationSidebarGroups.Project("a-1", [],
        _state.AgentId, _state.Sections, _state.Assignments, _state.GroupOrder, SelectionSource.UserClick);

    [Fact]
    public async Task Empty_State_RendersFourManageablePeersWithoutParent()
    {
        _handler.Snapshot = """{"sections":[],"assignments":{}}""";
        var cut = await Render();
        cut.FindAll(".conversation-group").Count.ShouldBe(4);
        cut.FindAll("[data-testid=section-actions-trigger]").Count.ShouldBe(4);
        cut.Markup.ShouldNotContain("My sections");
        cut.FindAll("[data-testid=section-add-btn]").ShouldBeEmpty();
    }

    [Fact]
    public async Task Populated_RendersOwnedName_ButNeverManufacturesAssignmentRows()
    {
        var cut = await Render();
        cut.Find("[data-testid=section-name]").TextContent.ShouldBe("Work");
        cut.FindAll("[data-conversation-id]").ShouldBeEmpty();
        cut.Find("[data-testid=conversation-section-empty]").TextContent.ShouldContain("No conversations");
    }

    [Fact]
    public async Task Collapsed_Section_HidesBody_AndTogglePersists()
    {
        var cut = await Render();
        cut.Find("[data-section-id=sec_1] [data-testid=section-collapse-toggle]").GetAttribute("aria-expanded").ShouldBe("true");
        cut.FindAll("[data-testid=conversation-section-body]").ShouldHaveSingleItem();
        // The PATCH's following GET must return the authoritative collapsed state, and the
        // standalone parent's Groups parameter must be reprojected just as MainLayout does.
        _handler.Snapshot = _handler.Snapshot.Replace("\"isCollapsed\":false", "\"isCollapsed\":true", StringComparison.Ordinal);
        await cut.Find("[data-section-id=sec_1] [data-testid=section-collapse-toggle]").ClickAsync(new());
        cut.Render(p => p.Add(c => c.Groups, Groups()));
        _state.Sections.Single().IsCollapsed.ShouldBeTrue();
        cut.Find("[data-section-id=sec_1] [data-testid=section-collapse-toggle]").GetAttribute("aria-expanded").ShouldBe("false");
        cut.FindAll("[data-testid=conversation-section-body]").ShouldBeEmpty();
        _handler.Requests.ShouldContain(r => r.Method == "PATCH" && r.Path.EndsWith("/sec_1", StringComparison.Ordinal));
        using var body = JsonDocument.Parse(_handler.Requests.Single(r => r.Method == "PATCH").Body);
        body.RootElement.GetProperty("isCollapsed").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Create_EnterPostsOnce_AndEscapeCancels()
    {
        var cut = await Render(controls: true);
        cut.Find("[data-testid=section-add-btn]").Click();
        cut.Find("[data-testid=section-create-input]").Input("Personal");
        cut.Find("[data-testid=section-create-input]").KeyDown("Enter");
        _handler.Requests.Count(r => r.Method == "POST").ShouldBe(1);
        cut.FindAll("[data-testid=section-create-input]").ShouldBeEmpty();
        cut.Find("[data-testid=section-add-btn]").Click();
        cut.Find("[data-testid=section-create-input]").KeyDown("Escape");
        cut.FindAll("[data-testid=section-create-input]").ShouldBeEmpty();
        _handler.Requests.Count(r => r.Method == "POST").ShouldBe(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task Create_RejectsBlankOrOverlongNames(int length)
    {
        var cut = await Render(controls: true);
        cut.Find("[data-testid=section-add-btn]").Click();
        cut.Find("[data-testid=section-create-input]").Input(length == 0 ? "   " : new string('x', length));
        cut.Find("[data-testid=section-create-input]").KeyDown("Enter");
        _handler.Requests.ShouldNotContain(r => r.Method == "POST");
        cut.Find("[role=alert]").TextContent.ShouldContain("1 to 100");
    }

    [Fact]
    public async Task Rename_UsesStateWriter_AndPersistsTrimmedName()
    {
        var cut = await Render();
        cut.Find("[data-section-id=sec_1] [data-testid=section-actions-trigger]").Click();
        cut.Find("[data-testid=section-rename-btn]").Click();
        cut.Find("[data-testid=section-rename-input]").Input(" New ");
        cut.Find("[data-testid=section-rename-input]").KeyDown("Enter");
        var request = _handler.Requests.Single(r => r.Method == "PATCH");
        using var body = JsonDocument.Parse(request.Body);
        body.RootElement.GetProperty("name").GetString().ShouldBe("New");
    }

    [Fact]
    public async Task Move_CrossesSystemBoundary_WithEveryCanonicalSlotInPayload()
    {
        var cut = await Render();
        cut.Find("[data-section-id=sec_1] [data-testid=section-actions-trigger]").Click();
        cut.Find("[data-testid=section-move-up]").Click();
        var request = _handler.Requests.Single(r => r.Method == "PUT");
        request.Path.ShouldEndWith("/group-order");
        using var body = JsonDocument.Parse(request.Body);
        var order = body.RootElement.GetProperty("groupOrder").EnumerateArray().ToArray();
        order.Length.ShouldBe(5);
        order[3].GetProperty("sectionId").GetString().ShouldBe("sec_1");
        order[4].GetProperty("system").GetString().ShouldBe("Webhooks");
    }

    [Fact]
    public async Task Delete_ConfirmsNaturalRestoration_NotConversationDeletion()
    {
        _ctx.JSInterop.Setup<bool>("confirm", _ => true).SetResult(true);
        var cut = await Render();
        cut.Find("[data-section-id=sec_1] [data-testid=section-actions-trigger]").Click();
        cut.Find("[data-testid=section-delete-btn]").Click();
        _handler.Requests.ShouldContain(r => r.Method == "DELETE" && r.Path.EndsWith("/sec_1", StringComparison.Ordinal));
        var confirmation = _ctx.JSInterop.Invocations.Single(i => i.Identifier == "confirm");
        var confirmationText = Assert.IsType<string>(confirmation.Arguments[0]?.ToString());
        confirmationText.ShouldContain("natural built-in homes");
        confirmationText.ShouldContain("not deleted");
    }

    [Fact]
    public async Task PendingDelete_AgentSwitchDiscardsConfirmation_EvenAfterSwitchingBack()
    {
        var pending = _ctx.JSInterop.Setup<bool>("confirm", _ => true);
        var cut = await Render();
        cut.Find("[data-section-id=sec_1] [data-testid=section-actions-trigger]").Click();
        var click = cut.Find("[data-testid=section-delete-btn]").ClickAsync(new());
        await cut.InvokeAsync(async () => await _state.EnsureLoadedAsync("b-2"));
        cut.Render(p => p.Add(c => c.AgentId, "b-2").Add(c => c.DraftScope, "b"));
        await cut.InvokeAsync(async () => await _state.EnsureLoadedAsync("a-1"));
        cut.Render(p => p.Add(c => c.AgentId, "a-1").Add(c => c.DraftScope, "a-again"));
        pending.SetResult(true);
        await click;
        _handler.Requests.ShouldNotContain(r => r.Method == "DELETE");
    }

    [Fact]
    public async Task NavigationScopeChange_DiscardsCreateDraft()
    {
        var cut = await Render(controls: true);
        cut.Find("[data-testid=section-add-btn]").Click();
        cut.Find("[data-testid=section-create-input]").Input("Unsaved");
        cut.Render(p => p.Add(c => c.DraftScope, "next-route"));
        cut.FindAll("[data-testid=section-create-input]").ShouldBeEmpty();
        _handler.Requests.ShouldNotContain(r => r.Method == "POST");
    }

    [Fact]
    public async Task Unavailable_DisablesEdits_AndExplicitRetryDoesNotRepost()
    {
        _handler.FailReads = true;
        var cut = await Render();
        cut.Find("[data-testid=sections-unavailable]").ShouldNotBeNull();
        cut.FindAll("[data-testid=section-actions-trigger]").ShouldAllBe(e => e.HasAttribute("disabled"));
        _handler.FailReads = false;
        cut.Find("[data-testid=sections-refresh]").Click();
        cut.WaitForAssertion(() => _state.CanMutate.ShouldBeTrue());
        _handler.Requests.ShouldAllBe(r => r.Method == "GET");
    }

    [Fact]
    public async Task Reconciliation_ExposesRefreshAndDisablesUnsafeEdits()
    {
        _handler.Snapshot = """{"sections":[],"assignments":{},"groupOrder":[{"system":"Pinned"}]}""";
        var cut = await Render();
        cut.Find("[data-testid=sections-reconciliation]").ShouldNotBeNull();
        cut.FindAll("[data-testid=section-actions-trigger]").ShouldAllBe(e => e.HasAttribute("disabled"));
        cut.FindAll(".conversation-group").Count.ShouldBe(4);
        _handler.Requests.ShouldAllBe(r => r.Method == "GET");
    }

    [Fact]
    public async Task UnsupportedOrder_RequiresExplicitRefresh_WithoutFallbackPut()
    {
        _handler.WriteStatus = HttpStatusCode.NotFound;
        var cut = await Render();
        cut.Find("[data-section-id=sec_1] [data-testid=section-actions-trigger]").Click();
        cut.Find("[data-testid=section-move-up]").Click();
        cut.Find("[data-testid=sections-unsupported]").ShouldNotBeNull();
        cut.Find("[data-testid=sections-mutation-error]").ShouldNotBeNull();
        cut.FindAll("[data-testid=section-actions-trigger]").ShouldAllBe(e => e.HasAttribute("disabled"));
        cut.Find("[data-testid=sections-refresh]").Click();
        _handler.Requests.Count(r => r.Method == "PUT").ShouldBe(1);
        _handler.Requests.ShouldNotContain(r => r.Path.EndsWith("/order", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SavedCreate_FailedRefresh_ClosesDraftAndPreventsRepost()
    {
        var cut = await Render(controls: true);
        cut.Find("[data-testid=section-add-btn]").Click();
        cut.Find("[data-testid=section-create-input]").Input("Personal");
        _handler.FailReads = true;
        cut.Find("[data-testid=section-create-input]").KeyDown("Enter");
        _state.SavedButRefreshFailed.ShouldBeTrue();
        cut.FindAll("[data-testid=section-create-input]").ShouldBeEmpty();
        cut.Find("[data-testid=section-add-btn]").HasAttribute("disabled").ShouldBeTrue();
        _handler.Requests.Count(r => r.Method == "POST").ShouldBe(1);
        cut.Render(p => p.Add(c => c.CreateOnly, false));
        cut.Find("[data-testid=sections-saved-refresh-failed]").TextContent.ShouldContain("do not save again");
    }

    [Fact]
    public async Task PendingCreate_EnterCannotRepost_AndAgentSwitchCannotRepaintOldDraft()
    {
        _handler.HoldWrites = true;
        var cut = await Render(controls: true);
        cut.Find("[data-testid=section-add-btn]").Click();
        cut.Find("[data-testid=section-create-input]").Input("Personal");
        var save = cut.Find("[data-testid=section-create-input]").KeyDownAsync(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });
        await _handler.WriteEntered.Task;
        await cut.InvokeAsync(() => _state.CreateAsync("Duplicate"));
        _handler.Requests.Count(r => r.Method == "POST").ShouldBe(1);
        _handler.Snapshot = """{"sections":[],"assignments":{}}""";
        await cut.InvokeAsync(() => _state.EnsureLoadedAsync("b-2"));
        cut.Render(p => p.Add(c => c.AgentId, "b-2").Add(c => c.DraftScope, "new-agent"));
        _handler.ReleaseWrite.SetResult(true);
        await save;
        _state.AgentId.ShouldBe("b-2");
        cut.FindAll("[data-testid=section-create-input]").ShouldBeEmpty();
        _handler.Requests.ShouldNotContain(r => r.Method == "POST" && r.Path.Contains("b-2", StringComparison.Ordinal));
        _handler.Requests.Count(r => r.Method == "GET" && r.Path.Contains("a-1", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task Loading_RendersStatusAndDisablesEditingUntilExplicitReadCompletes()
    {
        await _state.EnsureLoadedAsync("a-1");
        _handler.HoldReads = true;
        var load = _state.ReloadAsync();
        await _handler.ReadEntered.Task;
        var cut = _ctx.Render<ConversationSectionsPanel>(p => p.Add(c => c.AgentId, "a-1").Add(c => c.Groups, Groups()));
        cut.Find("[data-testid=sections-loading]").TextContent.ShouldContain("Loading");
        cut.FindAll("[data-testid=section-actions-trigger]").ShouldAllBe(e => e.HasAttribute("disabled"));
        cut.Find("[data-testid=sections-refresh]").HasAttribute("disabled").ShouldBeTrue();
        _handler.ReleaseRead.SetResult(true);
        await load;
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=sections-loading]").ShouldBeEmpty());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AuthoritativeCustomCollapse_RendersBodyOnlyWhenExpanded(bool collapsed)
    {
        _handler.Snapshot = """{"sections":[{"sectionId":"sec_1","agentId":"a-1","name":"Work","order":0,"isCollapsed":false}],"assignments":{}}"""
            .Replace("\"isCollapsed\":false", $"\"isCollapsed\":{collapsed.ToString().ToLowerInvariant()}", StringComparison.Ordinal);
        var cut = await Render();
        _state.Sections.Single().IsCollapsed.ShouldBe(collapsed);
        cut.Find("[data-section-id=sec_1] [data-testid=section-collapse-toggle]").GetAttribute("aria-expanded")
            .ShouldBe(collapsed ? "false" : "true");
        cut.FindAll("[data-testid=conversation-section-body]").Count.ShouldBe(collapsed ? 0 : 1);
        _handler.Requests.ShouldAllBe(r => r.Method == "GET");
    }

    [Theory]
    [InlineData("conversationActionMenu.close", "route", "section-delete-btn")]
    [InlineData("conversationActionMenu.focus", "mode", "section-move-up")]
    [InlineData("conversationActionMenu.close", "agent-aba", "section-rename-btn")]
    [InlineData("conversationActionMenu.focus", "dispose", "section-delete-btn")]
    public async Task HeldMenuAction_ParentInvalidationRejectsLateCallback(string identifier, string transition, string action)
    {
        var cut = await Render();
        cut.Find("[data-section-id=sec_1] [data-testid=section-actions-trigger]").Click();
        var pending = _ctx.JSInterop.SetupVoid(identifier, _ => true);
        var click = cut.Find($"[data-testid={action}]").ClickAsync(new());
        cut.WaitForAssertion(() => pending.Invocations.ShouldHaveSingleItem());
        pending.Invocations.Single().Arguments[0].ShouldBe(_ctx.JSInterop.Invocations
            .Last(i => i.Identifier == "conversationActionMenu.open").Arguments[0]);
        if (transition == "agent-aba")
        {
            await cut.InvokeAsync(() => _state.EnsureLoadedAsync("b-2"));
            await cut.InvokeAsync(() => _state.EnsureLoadedAsync("a-1"));
        }
        else if (transition == "dispose")
        {
            // Deliberately leave the detached child parameters unchanged: parent is a second guard.
            await cut.InvokeAsync(cut.Instance.Dispose);
        }
        else
        {
            // MainLayout calls this synchronously before route/mode rerender/remount.
            await cut.InvokeAsync(cut.Instance.ResetDrafts);
        }
        pending.SetVoidResult();
        await click;
        if (identifier == "conversationActionMenu.close")
            _ctx.JSInterop.Invocations.ShouldNotContain(i => i.Identifier == "conversationActionMenu.focus");
        _ctx.JSInterop.Invocations.ShouldNotContain(i => i.Identifier == "confirm");
        _handler.Requests.ShouldAllBe(r => r.Method == "GET");
        cut.FindAll("[data-testid=section-rename-input]").ShouldBeEmpty();
    }

    [Fact]
    public async Task HeldMenuMove_CanonicalOrderChangeRejectsSavedIndices()
    {
        var cut = await Render();
        cut.Find("[data-section-id=sec_1] [data-testid=section-actions-trigger]").Click();
        var pending = _ctx.JSInterop.SetupVoid("conversationActionMenu.close", _ => true);
        var click = cut.Find("[data-testid=section-move-up]").ClickAsync(new());
        cut.WaitForAssertion(() => pending.Invocations.ShouldHaveSingleItem());
        pending.Invocations.Single().Arguments[0].ShouldBe(_ctx.JSInterop.Invocations
            .Last(i => i.Identifier == "conversationActionMenu.open").Arguments[0]);
        // Same agent and same panel generation; only the authoritative ordering changes.
        _handler.Snapshot = """{"sections":[{"sectionId":"sec_1","agentId":"a-1","name":"Work","order":0,"isCollapsed":false}],"assignments":{},"groupOrder":[{"sectionId":"sec_1"},{"system":"Pinned"},{"system":"Conversations"},{"system":"Scheduled"},{"system":"Webhooks"}]}""";
        await cut.InvokeAsync(() => _state.ReloadAsync());
        pending.SetVoidResult();
        await click;
        _handler.Requests.ShouldAllBe(r => r.Method == "GET");
    }

    private sealed record RecordedRequest(string Method, string Path, string Body);
    private sealed class StubHandler : HttpMessageHandler
    {
        public string Snapshot { get; set; } = """{"sections":[{"sectionId":"sec_1","agentId":"a-1","name":"Work","order":0,"isCollapsed":false}],"assignments":{"unloaded":"sec_1"}}""";
        public bool FailReads { get; set; }
        public bool HoldWrites { get; set; }
        public bool HoldReads { get; set; }
        public TaskCompletionSource<bool> WriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HttpStatusCode WriteStatus { get; set; } = HttpStatusCode.OK;
        public List<RecordedRequest> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? throw new InvalidOperationException("Missing request URI");
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new(request.Method.Method, path, body));
            if (HoldWrites && request.Method != HttpMethod.Get)
            {
                WriteEntered.TrySetResult(true);
                await ReleaseWrite.Task;
            }
            if (HoldReads && request.Method == HttpMethod.Get)
            {
                ReadEntered.TrySetResult(true);
                await ReleaseRead.Task;
            }
            var json = request.Method == HttpMethod.Get ? Snapshot : request.Method == HttpMethod.Post
                ? """{"sectionId":"sec_new","agentId":"a-1","name":"Personal","order":1,"isCollapsed":false}""" : "{}";
            if (path.EndsWith("/group-order", StringComparison.Ordinal))
            {
                using var document = JsonDocument.Parse(body);
                json = document.RootElement.GetProperty("groupOrder").GetRawText();
            }
            return new HttpResponseMessage(FailReads && request.Method == HttpMethod.Get ? HttpStatusCode.ServiceUnavailable : request.Method == HttpMethod.Get ? HttpStatusCode.OK : WriteStatus)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
