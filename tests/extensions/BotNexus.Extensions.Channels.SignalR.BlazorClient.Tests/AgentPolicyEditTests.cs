using System.Net;
using System.Text.Json.Nodes;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class AgentPolicyEditTests : IDisposable
{
    private readonly BunitContext _context = new();
    private readonly Handler _handler = new();

    public AgentPolicyEditTests()
    {
        _context.Services.AddSingleton(new HttpClient(_handler) { BaseAddress = new Uri("http://localhost/") });
        _context.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void UnrelatedEdit_UsesOneChangedFieldPatch_WithoutMaterializingPolicy()
    {
        var cut = Render();
        Assert.True(cut.Find("button.primary").HasAttribute("disabled"));
        cut.Find("input[value='Sample']").Input("Renamed");
        cut.Find("button.primary").Click();
        cut.WaitForAssertion(() => Assert.Single(_handler.Writes));
        var (method, path, json) = _handler.Writes.Single();
        Assert.Equal("PATCH", method);
        Assert.Equal("/api/agents/sample/configuration", path);
        var body = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidOperationException();
        Assert.Equal("Renamed", body["displayName"]?.GetValue<string>());
        Assert.Single(body);
    }

    [Fact]
    public void TimeoutAndAccessEdits_AreOneAtomicPatch_AndFailuresStayDirty()
    {
        _handler.FailWrites = true;
        var cut = Render();
        cut.Find("input[type=number][min='1']").Input("91");
        cut.FindAll("select").First(e => e.QuerySelector("option[value='allowlist']") is not null).Change("all");
        cut.Find("button.primary").Click();
        cut.WaitForAssertion(() => Assert.Contains("Save failed", cut.Markup));
        var body = JsonNode.Parse(_handler.Writes.Single().Json)?.AsObject() ?? throw new InvalidOperationException();
        Assert.Equal(91, body["toolTimeoutSeconds"]?.GetValue<int>());
        Assert.Equal("all", body["sessionAccess"]?["level"]?.GetValue<string>());
        Assert.False(body.ContainsKey("toolPolicy"));
        Assert.False(cut.Find("button.primary").HasAttribute("disabled"));
    }

    [Fact]
    public void CanonicalPolicyRead_RendersAllControls_AndOnlyEditedListIsWritten()
    {
        _handler.Config = """{"displayName":"Sample","provider":"test","model":"test","toolTimeoutSeconds":90,"sessionAccess":{"level":"allowlist","allowedAgents":["peer"]},"conversationAccess":{"level":"allowlist","allowedAgents":["peer"]},"toolPolicy":{"alwaysApprove":["read"],"neverApprove":["shell"],"denied":["delete"],"askFallback":"deny"}}""";
        var cut = Render();
        Assert.Contains("/api/agents/sample/configuration", _handler.Reads);
        Assert.Equal("90", cut.Find("input[type=number][min='1']").GetAttribute("value"));
        Assert.Equal(2, cut.FindAll("select[value='allowlist']").Count);
        Assert.Equal(2, cut.FindAll(".agent-tag").Count(e => e.TextContent.Contains("peer")));
        Assert.True(cut.Find("button.primary").HasAttribute("disabled"));
        cut.Find("button[aria-label='Remove denied tool delete']").Click();
        cut.Find("button.primary").Click();
        cut.WaitForAssertion(() => Assert.Single(_handler.Writes));
        Assert.Equal("{\"toolPolicy\":{\"denied\":[]}}", _handler.Writes.Single().Json);
    }

    [Fact]
    public void NewPolicyList_DoesNotWriteSiblingDefaults()
    {
        var cut = Render();
        var section = cut.FindAll("details").Single(e => e.QuerySelector("summary")?.TextContent == "Tool Policy");
        section.QuerySelectorAll("input")[2].Change("delete");
        section.QuerySelectorAll(".agent-tag-add-row button")[2].Click();
        cut.Find("button.primary").Click();
        cut.WaitForAssertion(() => Assert.Single(_handler.Writes));
        Assert.Equal("{\"toolPolicy\":{\"denied\":[\"delete\"]}}", _handler.Writes.Single().Json);
    }

    [Fact]
    public void ExplicitOwnAccess_DoesNotDisappearAsAnEmptyPatch()
    {
        var cut = Render();
        cut.FindAll("select").First(e => e.QuerySelector("option[value='allowlist']") is not null).Change("own");
        cut.Find("button.primary").Click();
        cut.WaitForAssertion(() => Assert.Single(_handler.Writes));
        Assert.Equal("{\"sessionAccess\":{\"level\":\"own\"}}", _handler.Writes.Single().Json);
    }

    [Fact]
    public void ExplicitOwnConversationRestriction_FromNullFallback_EnablesSave()
    {
        _handler.Config = """{"displayName":"Sample","sessionAccess":{"level":"all"},"conversationAccess":{"level":null}}""";
        var cut = Render();
        Assert.True(cut.Find("button.primary").HasAttribute("disabled"));
        cut.FindAll("select").Where(e => e.QuerySelector("option[value='allowlist']") is not null).ElementAt(1).Change("own");
        Assert.False(cut.Find("button.primary").HasAttribute("disabled"));
        cut.Find("button.primary").Click();
        cut.WaitForAssertion(() => Assert.Single(_handler.Writes));
        Assert.Equal("{\"conversationAccess\":{\"level\":\"own\"}}", _handler.Writes.Single().Json);
        Assert.True(cut.Find("button.primary").HasAttribute("disabled"));
    }

    [Theory]
    [InlineData(0, "alwaysApprove")]
    [InlineData(1, "neverApprove")]
    [InlineData(2, "denied")]
    public void EachNewToolPolicyList_WritesOnlyThatLeaf(int index, string name)
    {
        var cut = Render();
        var section = cut.FindAll("details").Single(e => e.QuerySelector("summary")?.TextContent == "Tool Policy");
        section.QuerySelectorAll("input")[index].Change("tool");
        section.QuerySelectorAll(".agent-tag-add-row button")[index].Click();
        cut.Find("button.primary").Click();
        cut.WaitForAssertion(() => Assert.Single(_handler.Writes));
        var body = JsonNode.Parse(_handler.Writes.Single().Json)?.AsObject() ?? throw new InvalidOperationException();
        var policy = Assert.IsType<JsonObject>(body["toolPolicy"]);
        Assert.Single(body);
        Assert.Single(policy);
        Assert.Equal("tool", policy[name]?[0]?.GetValue<string>());
        Assert.True(cut.Find("button.primary").HasAttribute("disabled"));
    }

    [Theory]
    [InlineData("always-approved", "read", "alwaysApprove")]
    [InlineData("never-approved", "shell", "neverApprove")]
    [InlineData("denied", "delete", "denied")]
    public void RemovingLastToolPolicyEntry_ExplicitlyClearsOnlyThatList(string label, string tool, string field)
    {
        _handler.Config = """{"displayName":"Sample","toolPolicy":{"alwaysApprove":["read"],"neverApprove":["shell"],"denied":["delete"]}}""";
        var cut = Render();
        cut.Find($"button[aria-label='Remove {label} tool {tool}']").Click();
        cut.Find("button.primary").Click();
        cut.WaitForAssertion(() => Assert.Single(_handler.Writes));
        var body = JsonNode.Parse(_handler.Writes.Single().Json)?.AsObject() ?? throw new InvalidOperationException();
        var policy = Assert.IsType<JsonObject>(body["toolPolicy"]);
        Assert.Single(policy);
        Assert.Empty(Assert.IsType<JsonArray>(policy[field]));
    }

    [Fact]
    public void ClearingBothAllowedAgentLists_AndPositiveTimeout_AreExplicitInOneBatch()
    {
        _handler.Config = """{"displayName":"Sample","toolTimeoutSeconds":90,"sessionAccess":{"level":"allowlist","allowedAgents":["peer"]},"conversationAccess":{"level":"allowlist","allowedAgents":["peer"]}}""";
        var cut = Render();
        cut.Find(".agent-tag button").Click();
        cut.Find(".agent-tag button").Click();
        cut.Find("input[type=number][min='1']").Input("1");
        cut.Find("button.primary").Click();
        cut.WaitForAssertion(() => Assert.Single(_handler.Writes));
        var body = JsonNode.Parse(_handler.Writes.Single().Json)?.AsObject() ?? throw new InvalidOperationException();
        Assert.Equal(1, body["toolTimeoutSeconds"]?.GetValue<int>());
        Assert.Empty(Assert.IsType<JsonArray>(body["sessionAccess"]?["allowedAgents"]));
        Assert.Empty(Assert.IsType<JsonArray>(body["conversationAccess"]?["allowedAgents"]));
        Assert.False(body.ContainsKey("toolPolicy"));
    }

    [Fact]
    public void ZeroTimeout_IsRejectedLocally_AndClearingTimeoutWritesExplicitNull()
    {
        _handler.Config = """{"displayName":"Sample","toolTimeoutSeconds":90}""";
        var cut = Render();
        cut.Find("input[type=number][min='1']").Input("0");
        cut.Find("button.primary").Click();
        Assert.Empty(_handler.Writes);
        Assert.Contains("Tool timeout must be positive or unset", cut.Markup);
        cut.Find("input[type=number][min='1']").Input("");
        cut.Find("button.primary").Click();
        cut.WaitForAssertion(() => Assert.Single(_handler.Writes));
        Assert.Equal("""{"toolTimeoutSeconds":null}""", _handler.Writes.Single().Json);
    }

    [Fact]
    public void UnsupportedControls_AreNotEditable_AndLoadingDoesNotClearCapabilities()
    {
        _handler.Config = """{"displayName":"Sample","provider":"test","model":"test","thinking":"high","contextWindow":128000,"enabled":false}""";
        var cut = Render();
        Assert.True(cut.Find("input[type=checkbox]").HasAttribute("disabled"));
        Assert.Contains("#4964", cut.Markup);
        Assert.DoesNotContain("System Prompt</summary>", cut.Markup);
        Assert.True(cut.Find("button.primary").HasAttribute("disabled"));
        cut.Find("input[value='Sample']").Input("Renamed");
        cut.Find("button.primary").Click();
        cut.WaitForAssertion(() => Assert.Single(_handler.Writes));
        Assert.Equal("""{"displayName":"Renamed"}""", _handler.Writes.Single().Json);
    }

    private IRenderedComponent<AgentDetailPanel> Render()
    {
        var cut = _context.Render<AgentDetailPanel>(p => p.Add(c => c.AgentId, "sample"));
        cut.WaitForState(() => !cut.Markup.Contains("Loading agent"));
        return cut;
    }

    public void Dispose() => _context.Dispose();

    private sealed class Handler : HttpMessageHandler
    {
        public bool FailWrites { get; set; }
        public string Config { get; set; } = """{"displayName":"Sample","provider":"test","model":"test"}""";
        public List<string> Reads { get; } = [];
        public List<(string Method, string Path, string Json)> Writes { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (request.Method != HttpMethod.Get)
            {
                Writes.Add((request.Method.Method, path, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
                return Reply(FailWrites ? HttpStatusCode.BadRequest : HttpStatusCode.OK, "{}");
            }
            Reads.Add(path);
            return Reply(HttpStatusCode.OK, path is "/api/agents/sample" or "/api/agents/sample/configuration"
                ? Config
                : "[]");
        }
        private static HttpResponseMessage Reply(HttpStatusCode status, string json) => new(status)
        { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
    }
}
