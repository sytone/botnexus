using System.Net;
using System.Text;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// bUnit component tests for <see cref="ConversationSectionsPanel"/> - the user-defined conversation
/// sections sidebar UI (issue #2124). Covers the empty and populated render states plus the create,
/// rename, collapse, reorder, delete, and remove-conversation interactions, each asserting the
/// component issues the matching server call (state is persisted server-side, not in local storage).
/// </summary>
public sealed class ConversationSectionsPanelTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly StubHandler _handler = new();

    public ConversationSectionsPanelTests()
    {
        var http = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost/") };
        _ctx.Services.AddSingleton(http);
        _ctx.Services.AddSingleton(new SectionsApiClient(http));
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.JSInterop.Setup<bool>("confirm", _ => true).SetResult(true);
    }

    public void Dispose() => _ctx.Dispose();

    private IRenderedComponent<ConversationSectionsPanel> Render(string agentId = "a-1") =>
        _ctx.Render<ConversationSectionsPanel>(p => p.Add(c => c.AgentId, agentId));

    [Fact]
    public void Empty_State_Renders_Header_And_Add_Button()
    {
        _handler.SetJson("GET", "/api/agents/a-1/sections", """{"sections":[],"assignments":{}}""");

        var cut = Render();

        cut.Find("[data-testid=conversation-sections]").ShouldNotBeNull();
        cut.Find("[data-testid=section-add-btn]").ShouldNotBeNull();
        cut.FindAll("[data-testid=conversation-section]").Count.ShouldBe(0);
    }

    [Fact]
    public void Populated_Renders_Section_And_Members()
    {
        _handler.SetJson("GET", "/api/agents/a-1/sections",
            """{"sections":[{"sectionId":"sec_1","agentId":"a-1","name":"Work","order":0,"isCollapsed":false}],"assignments":{"c_1":"sec_1"}}""");

        var cut = Render();

        cut.Find("[data-testid=section-name]").TextContent.ShouldBe("Work");
        cut.FindAll("[data-testid=section-conversation-item]").Count.ShouldBe(1);
        cut.Find("[data-testid=section-conversation-item]").GetAttribute("data-conversation-id").ShouldBe("c_1");
    }

    [Fact]
    public void Collapsed_Section_Hides_Body()
    {
        _handler.SetJson("GET", "/api/agents/a-1/sections",
            """{"sections":[{"sectionId":"sec_1","agentId":"a-1","name":"Work","order":0,"isCollapsed":true}],"assignments":{"c_1":"sec_1"}}""");

        var cut = Render();

        cut.FindAll("[data-testid=conversation-section-body]").Count.ShouldBe(0);
    }

    [Fact]
    public void Create_Section_Posts_To_Server()
    {
        _handler.SetJson("GET", "/api/agents/a-1/sections", """{"sections":[],"assignments":{}}""");
        _handler.SetJson("POST", "/api/agents/a-1/sections",
            """{"sectionId":"sec_new","agentId":"a-1","name":"Personal","order":0,"isCollapsed":false}""", HttpStatusCode.Created);

        var cut = Render();
        cut.Find("[data-testid=section-add-btn]").Click();
        cut.Find("[data-testid=section-create-input]").Input("Personal");
        cut.Find("[data-testid=section-create-save]").Click();

        _handler.Requests.ShouldContain(r => r.Method == "POST" && r.Path == "/api/agents/a-1/sections");
    }

    [Fact]
    public void Rename_Section_Patches_Server()
    {
        _handler.SetJson("GET", "/api/agents/a-1/sections",
            """{"sections":[{"sectionId":"sec_1","agentId":"a-1","name":"Old","order":0,"isCollapsed":false}],"assignments":{}}""");
        _handler.SetStatus("PATCH", "/api/agents/a-1/sections/sec_1", HttpStatusCode.OK);

        var cut = Render();
        cut.Find("[data-testid=section-rename-btn]").Click();
        cut.Find("[data-testid=section-rename-input]").Input("New");
        cut.Find("[data-testid=section-rename-save]").Click();

        _handler.Requests.ShouldContain(r => r.Method == "PATCH" && r.Path == "/api/agents/a-1/sections/sec_1");
    }

    [Fact]
    public void Toggle_Collapse_Patches_Server()
    {
        _handler.SetJson("GET", "/api/agents/a-1/sections",
            """{"sections":[{"sectionId":"sec_1","agentId":"a-1","name":"Work","order":0,"isCollapsed":false}],"assignments":{}}""");
        _handler.SetStatus("PATCH", "/api/agents/a-1/sections/sec_1", HttpStatusCode.OK);

        var cut = Render();
        cut.Find("[data-testid=section-collapse-toggle]").Click();

        _handler.Requests.ShouldContain(r => r.Method == "PATCH" && r.Path == "/api/agents/a-1/sections/sec_1");
    }

    [Fact]
    public void Reorder_Down_Puts_Order_To_Server()
    {
        _handler.SetJson("GET", "/api/agents/a-1/sections",
            """{"sections":[{"sectionId":"sec_1","agentId":"a-1","name":"A","order":0,"isCollapsed":false},{"sectionId":"sec_2","agentId":"a-1","name":"B","order":1,"isCollapsed":false}],"assignments":{}}""");
        _handler.SetStatus("PUT", "/api/agents/a-1/sections/order", HttpStatusCode.NoContent);

        var cut = Render();
        cut.FindAll("[data-testid=section-move-down]")[0].Click();

        _handler.Requests.ShouldContain(r => r.Method == "PUT" && r.Path == "/api/agents/a-1/sections/order");
    }

    [Fact]
    public void Delete_Section_Deletes_On_Server_After_Confirm()
    {
        _handler.SetJson("GET", "/api/agents/a-1/sections",
            """{"sections":[{"sectionId":"sec_1","agentId":"a-1","name":"Temp","order":0,"isCollapsed":false}],"assignments":{}}""");
        _handler.SetStatus("DELETE", "/api/agents/a-1/sections/sec_1", HttpStatusCode.NoContent);

        var cut = Render();
        cut.Find("[data-testid=section-delete-btn]").Click();

        _handler.Requests.ShouldContain(r => r.Method == "DELETE" && r.Path == "/api/agents/a-1/sections/sec_1");
    }

    [Fact]
    public void Remove_Conversation_Deletes_Assignment_On_Server()
    {
        _handler.SetJson("GET", "/api/agents/a-1/sections",
            """{"sections":[{"sectionId":"sec_1","agentId":"a-1","name":"Work","order":0,"isCollapsed":false}],"assignments":{"c_1":"sec_1"}}""");
        _handler.SetStatus("DELETE", "/api/agents/a-1/sections/conversations/c_1", HttpStatusCode.NoContent);

        var cut = Render();
        cut.Find("[data-testid=section-remove-conversation]").Click();

        _handler.Requests.ShouldContain(r => r.Method == "DELETE" && r.Path == "/api/agents/a-1/sections/conversations/c_1");
    }

    private sealed record RecordedRequest(string Method, string Path);

    private sealed class StubHandler : HttpMessageHandler
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
            Requests.Add(new RecordedRequest(method, path));

            if (_responses.TryGetValue($"{method} {path}", out var configured))
            {
                var msg = new HttpResponseMessage(configured.Status);
                if (configured.Body is not null)
                    msg.Content = new StringContent(configured.Body, Encoding.UTF8, "application/json");
                return Task.FromResult(msg);
            }

            // Default: empty OK so unconfigured calls (e.g. the reload GET after a mutation) don't throw.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"sections":[],"assignments":{}}""", Encoding.UTF8, "application/json")
            });
        }
    }
}
