using System.Net;
using System.Text.Json;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class AgentEnabledEditorTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Editor_LoadsEnabledState_AndSubmitsExplicitToggle(bool initial, bool desired)
    {
        using var context = new BunitContext();
        using var handler = new EditorHandler(initial);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        context.Services.AddSingleton(client);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = context.Render<AgentDetailPanel>(p => p.Add(c => c.AgentId, "assistant"));
        cut.WaitForState(() => cut.FindAll("input[type=checkbox]").Count > 0);
        var checkbox = cut.FindAll("input[type=checkbox]").First();
        checkbox.HasAttribute("checked").ShouldBe(initial);
        checkbox.Change(desired);
        await cut.FindAll("button").First(b => b.TextContent.Contains("Save Changes")).ClickAsync(new());
        var body = handler.Submitted ?? throw new InvalidOperationException("Expected PATCH.");
        handler.Method.ShouldBe(HttpMethod.Patch);
        handler.Path.ShouldBe("/api/agents/assistant/configuration");
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("enabled").GetBoolean().ShouldBe(desired);
        json.RootElement.EnumerateObject().Count().ShouldBe(1);
        cut.Markup.ShouldNotContain("Save failed");
        cut.Find("[role=status]").TextContent.ShouldBe(desired
            ? "Configuration saved. Enabled for new work; provider authentication is checked when work starts."
            : "Configuration saved. Disabled for new work; existing work is not cancelled.");
    }

    private sealed class EditorHandler(bool enabled) : HttpMessageHandler
    {
        public string? Submitted { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? Path { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath;
            if (request.Method == HttpMethod.Patch)
            {
                Method = request.Method;
                Path = path;
                Submitted = await (request.Content ?? throw new InvalidOperationException("Expected content.")).ReadAsStringAsync(cancellationToken);
                return Json(Submitted);
            }
            return Json(path is "/api/agents/assistant" or "/api/agents/assistant/configuration"
                ? JsonSerializer.Serialize(new { agentId = "assistant", displayName = "Assistant", apiProvider = "copilot", modelId = "gpt-4.1", enabled })
                : "[]");
        }
        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
    }
}
