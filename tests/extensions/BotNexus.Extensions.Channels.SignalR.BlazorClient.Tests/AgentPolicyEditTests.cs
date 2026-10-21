using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Abstractions.Models;
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
    public void EnabledControl_IsEditable_AndUnrelatedSavePreservesStateAndCapabilities()
    {
        _handler.Config = """{"displayName":"Sample","provider":"test","model":"test","thinking":"high","contextWindow":128000,"enabled":false}""";
        var cut = Render();
        Assert.False(cut.Find("input[type=checkbox]").HasAttribute("disabled"));
        Assert.False(cut.Find("input[type=checkbox]").HasAttribute("checked"));
        Assert.DoesNotContain("pending #4964", cut.Markup);
        Assert.DoesNotContain("System Prompt</summary>", cut.Markup);
        Assert.True(cut.Find("button.primary").HasAttribute("disabled"));
        cut.Find("input[value='Sample']").Input("Renamed");
        cut.Find("button.primary").Click();
        cut.WaitForAssertion(() => Assert.Single(_handler.Writes));
        Assert.Equal("""{"displayName":"Renamed"}""", _handler.Writes.Single().Json);
    }

    [Theory]
    [InlineData(false, "prompt")]
    [InlineData(true, "prompt")]
    [InlineData(false, "intervalMinutes")]
    [InlineData(true, "intervalMinutes")]
    public void NewHeartbeat_PromptOrIntervalEdit_PreservesRenderedDisabledState(bool explicitNull, string field)
    {
        _handler.Config = explicitNull ? """{"displayName":"Sample","heartbeat":null}""" : """{"displayName":"Sample"}""";
        var cut = Render();
        var section = cut.FindAll("details").Single(e => e.QuerySelector("summary")?.TextContent == "Heartbeat");
        Assert.False(section.QuerySelector("input[type=checkbox]")?.HasAttribute("checked"));
        Assert.Equal("60", section.QuerySelector("input[type=number]")?.GetAttribute("value"));
        Assert.True(cut.Find("button.primary").HasAttribute("disabled"));
        if (field == "prompt")
            section.QuerySelectorAll("textarea").Single().Input("Check tasks");
        else
            section.QuerySelectorAll("input[type=number]").Single().Input("45");
        cut.Find("button.primary").Click();
        cut.WaitForAssertion(() => Assert.Single(_handler.Writes));
        var body = JsonNode.Parse(_handler.Writes.Single().Json)?.AsObject() ?? throw new InvalidOperationException();
        Assert.Single(body);
        var heartbeat = Assert.IsType<JsonObject>(body["heartbeat"]);
        Assert.False(Assert.IsAssignableFrom<JsonValue>(heartbeat["enabled"]).GetValue<bool>());
        Assert.Equal(field == "prompt" ? 3 : 2, heartbeat.Count);
        var persisted = heartbeat.Deserialize<HeartbeatAgentConfig>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException();
        Assert.False(persisted.Enabled);
        Assert.Equal(field == "prompt" ? 60 : 45, persisted.IntervalMinutes);
        Assert.Equal(field == "prompt" ? "Check tasks" : null, persisted.Prompt);
        Assert.True(cut.Find("button.primary").HasAttribute("disabled"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewHeartbeat_Enable_PreservesRenderedInterval(bool explicitNull)
    {
        _handler.Config = explicitNull ? """{"displayName":"Sample","heartbeat":null}""" : """{"displayName":"Sample"}""";
        var cut = Render();
        var section = cut.FindAll("details").Single(e => e.QuerySelector("summary")?.TextContent == "Heartbeat");
        section.QuerySelectorAll("input[type=checkbox]")[0].Change(true);
        cut.Find("button.primary").Click();
        cut.WaitForAssertion(() => Assert.Single(_handler.Writes));
        var body = JsonNode.Parse(_handler.Writes.Single().Json)?.AsObject() ?? throw new InvalidOperationException();
        Assert.Single(body);
        var heartbeat = Assert.IsType<JsonObject>(body["heartbeat"]);
        Assert.Equal(60, heartbeat["intervalMinutes"]?.GetValue<int>());
        var persisted = heartbeat.Deserialize<HeartbeatAgentConfig>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException();
        Assert.True(persisted.Enabled);
        Assert.Equal(60, persisted.IntervalMinutes);
    }

    [Theory]
    [InlineData("""{"enabled":false,"intervalMinutes":75}""")]
    [InlineData("""{"enabled":false,"intervalMinutes":75,"quietHours":null}""")]
    public void NewQuietHours_TimezoneEdit_PreservesRenderedStart(string heartbeatJson)
    {
        _handler.Config = "{\"displayName\":\"Sample\",\"heartbeat\":" + heartbeatJson + "}";
        var cut = Render();
        var section = cut.FindAll("details").Single(e => e.QuerySelector("summary")?.TextContent == "Heartbeat");
        Assert.Equal("22:00", section.QuerySelector("input[placeholder='HH:mm']")?.GetAttribute("value"));
        section.QuerySelectorAll("input.cfg-input").Last().Input("Europe/London");
        cut.Find("button.primary").Click();
        cut.WaitForAssertion(() => Assert.Single(_handler.Writes));
        var body = JsonNode.Parse(_handler.Writes.Single().Json)?.AsObject() ?? throw new InvalidOperationException();
        Assert.Single(body);
        var heartbeat = Assert.IsType<JsonObject>(body["heartbeat"]);
        Assert.Single(heartbeat);
        var quietHours = Assert.IsType<JsonObject>(heartbeat["quietHours"]);
        Assert.Equal(2, quietHours.Count);
        var persisted = quietHours.Deserialize<QuietHoursConfig>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException();
        Assert.False(persisted.Enabled);
        Assert.Equal("22:00", persisted.Start);
        Assert.Equal("07:00", persisted.End);
        Assert.Equal("Europe/London", persisted.Timezone);
    }

    [Fact]
    public void ExistingSparseHeartbeat_PromptEdit_DisplaysRuntimeDefaultsAndRemainsLeafOnly()
    {
        _handler.Config = """{"displayName":"Sample","heartbeat":{}}""";
        var cut = Render();
        var section = cut.FindAll("details").Single(e => e.QuerySelector("summary")?.TextContent == "Heartbeat");
        section.QuerySelectorAll("input[type=checkbox]")[0].HasAttribute("checked").ShouldBeTrue();
        section.QuerySelector("input[type=number]")?.GetAttribute("value").ShouldBe("30");
        cut.Find("button.primary").HasAttribute("disabled").ShouldBeTrue();

        section.QuerySelectorAll("textarea").Single().Input("Check tasks");
        cut.Find("button.primary").Click();
        cut.WaitForAssertion(() => _handler.Writes.Count.ShouldBe(1));
        var (method, path, json) = _handler.Writes.Single();
        method.ShouldBe("PATCH");
        path.ShouldBe("/api/agents/sample/configuration");
        JsonNode.DeepEquals(JsonNode.Parse("""{"heartbeat":{"prompt":"Check tasks"}}"""), JsonNode.Parse(json)).ShouldBeTrue();
    }

    [Fact]
    public void ExistingSparseQuietHours_TimezoneEdit_DisplaysRuntimeDefaultsAndRemainsLeafOnly()
    {
        _handler.Config = """{"displayName":"Sample","heartbeat":{"quietHours":{}}}""";
        var cut = Render();
        var section = cut.FindAll("details").Single(e => e.QuerySelector("summary")?.TextContent == "Heartbeat");
        section.QuerySelectorAll("input[type=checkbox]")[0].HasAttribute("checked").ShouldBeTrue();
        section.QuerySelector("input[type=number]")?.GetAttribute("value").ShouldBe("30");
        section.QuerySelectorAll("input[type=checkbox]")[1].HasAttribute("checked").ShouldBeFalse();
        section.QuerySelectorAll("input[placeholder='HH:mm']")[0].GetAttribute("value").ShouldBe("23:00");
        section.QuerySelectorAll("input[placeholder='HH:mm']")[1].GetAttribute("value").ShouldBe("07:00");
        cut.Find("button.primary").HasAttribute("disabled").ShouldBeTrue();

        section.QuerySelectorAll("input.cfg-input").Last().Input("Europe/London");
        cut.Find("button.primary").Click();
        cut.WaitForAssertion(() => _handler.Writes.Count.ShouldBe(1));
        var (method, path, json) = _handler.Writes.Single();
        method.ShouldBe("PATCH");
        path.ShouldBe("/api/agents/sample/configuration");
        JsonNode.DeepEquals(JsonNode.Parse("""{"heartbeat":{"quietHours":{"timezone":"Europe/London"}}}"""), JsonNode.Parse(json)).ShouldBeTrue();
    }

    [Fact]
    public void ExistingHeartbeatAndQuietHours_EditsRemainLeafOnly()
    {
        _handler.Config = """{"displayName":"Sample","heartbeat":{"enabled":false,"intervalMinutes":75,"ackMaxChars":333,"future":"keep","quietHours":{"enabled":true,"start":"21:00","end":"06:00","timezone":"UTC","future":"keep"}}}""";
        var cut = Render();
        var section = cut.FindAll("details").Single(e => e.QuerySelector("summary")?.TextContent == "Heartbeat");
        section.QuerySelectorAll("textarea").Single().Input("Check tasks");
        section.QuerySelectorAll("input.cfg-input").Last().Input("Europe/London");
        cut.Find("button.primary").Click();
        cut.WaitForAssertion(() => Assert.Single(_handler.Writes));
        var body = JsonNode.Parse(_handler.Writes.Single().Json)?.AsObject() ?? throw new InvalidOperationException();
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"heartbeat":{"prompt":"Check tasks","quietHours":{"timezone":"Europe/London"}}}"""), body));
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
