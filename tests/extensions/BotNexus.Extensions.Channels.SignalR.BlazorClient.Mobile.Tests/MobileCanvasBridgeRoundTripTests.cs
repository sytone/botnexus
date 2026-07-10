using System.Net;
using System.Text.Json;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Gateway.Api.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Mirror of the desktop iframe -> store -> agent seam characterization tests (#1900) for the mobile
/// canvas panel. The mobile panel posts to the same <c>api/conversations/{id}/canvas-state/{key}</c>
/// route, so the same server route regression broke it. See
/// <c>CanvasBridgeRoundTripTests</c> in the desktop test project for the full rationale.
/// </summary>
public sealed class MobileCanvasBridgeRoundTripTests : IDisposable
{
    private const string ConversationId = "c_roundtrip_mobile";

    private readonly BunitContext _ctx = new();
    private readonly IClientStateStore _store = Substitute.For<IClientStateStore>();
    private readonly RouteBackedCanvasStateHandler _handler = new();

    public MobileCanvasBridgeRoundTripTests()
    {
        var agentState = new AgentState
        {
            AgentId = "agent-1",
            DisplayName = "Alpha",
            SessionId = "sess-1",
            ActiveConversationId = ConversationId,
            CanvasHtml = "<html><body>canvas</body></html>",
        };
        _store.GetAgent("agent-1").Returns(agentState);
        _store.GetConversation(ConversationId).Returns((ConversationState?)null);

        _ctx.Services.AddSingleton(_store);
        _ctx.Services.AddScoped(_ => new HttpClient(_handler) { BaseAddress = new Uri("http://localhost/") });
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task Mobile_iframe_set_then_get_round_trips_through_the_conversations_rest_route()
    {
        var cut = _ctx.Render<MobileCanvasPanel>(parameters => parameters
            .Add(x => x.AgentId, "agent-1")
            .Add(x => x.ConversationId, ConversationId)
            .Add(x => x.Open, true));

        var setJson = JsonSerializer.Serialize(new
        {
            type = "canvas-state-set",
            requestId = "req_1",
            key = "f1",
            value = "typed-value",
        });
        await cut.Instance.HandleCanvasMessage(setJson);

        _handler.Store.ContainsKey("f1").ShouldBe(true,
            "mobile iframe canvasState.set must persist through the api/conversations/.../canvas-state route (#1900)");

        var getJson = JsonSerializer.Serialize(new
        {
            type = "canvas-state-get",
            requestId = "req_2",
            key = "f1",
        });
        await cut.Instance.HandleCanvasMessage(getJson);

        _handler.Store["f1"].GetString().ShouldBe("typed-value");
    }

    [Fact]
    public async Task Mobile_iframe_write_scope_matches_the_active_conversation_rest_route()
    {
        var cut = _ctx.Render<MobileCanvasPanel>(parameters => parameters
            .Add(x => x.AgentId, "agent-1")
            .Add(x => x.ConversationId, ConversationId)
            .Add(x => x.Open, true));

        var setJson = JsonSerializer.Serialize(new
        {
            type = "canvas-state-set",
            requestId = "req_scope",
            key = "updatedAt",
            value = "2025",
        });
        await cut.Instance.HandleCanvasMessage(setJson);

        _handler.LastMatchedPath.ShouldNotBeNull();
        _handler.LastMatchedPath.ShouldBe($"api/conversations/{ConversationId}/canvas-state/updatedAt");
    }

    private sealed class RouteBackedCanvasStateHandler : HttpMessageHandler
    {
        public Dictionary<string, JsonElement> Store { get; } = new(StringComparer.Ordinal);
        public string? LastMatchedPath { get; private set; }

        private static readonly string BaseRoute = ResolveBaseRoute();
        private static readonly string SetKeyRoute =
            $"{BaseRoute}/{ResolveVerbTemplate(nameof(ConversationCanvasController.SetCanvasStateKey), typeof(HttpPostAttribute))}";
        private static readonly string GetKeyRoute =
            $"{BaseRoute}/{ResolveVerbTemplate(nameof(ConversationCanvasController.GetCanvasStateKey), typeof(HttpGetAttribute))}";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');

            if (request.Method == HttpMethod.Post && TryMatch(SetKeyRoute, path, out var setValues))
            {
                LastMatchedPath = path;
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(body);
                Store[setValues["key"]] = doc.RootElement.Clone();
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Get && TryMatch(GetKeyRoute, path, out var getValues))
            {
                LastMatchedPath = path;
                if (Store.TryGetValue(getValues["key"], out var value))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(value.GetRawText()),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static bool TryMatch(string template, string path, out Dictionary<string, string> values)
        {
            values = new Dictionary<string, string>(StringComparer.Ordinal);
            var tSegs = template.Split('/');
            var pSegs = path.Split('/');
            if (tSegs.Length != pSegs.Length)
                return false;

            for (var i = 0; i < tSegs.Length; i++)
            {
                var t = tSegs[i];
                if (t.StartsWith('{') && t.EndsWith('}'))
                    values[t.Trim('{', '}')] = Uri.UnescapeDataString(pSegs[i]);
                else if (!string.Equals(t, pSegs[i], StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        private static string ResolveBaseRoute()
        {
            var attr = typeof(ConversationCanvasController)
                .GetCustomAttributes(typeof(RouteAttribute), inherit: false)
                .Cast<RouteAttribute>()
                .Single();
            return attr.Template.Replace("[controller]", "ConversationCanvas", StringComparison.Ordinal);
        }

        private static string ResolveVerbTemplate(string methodName, Type verbAttributeType)
        {
            var method = typeof(ConversationCanvasController).GetMethod(methodName)!;
            var attr = method.GetCustomAttributes(verbAttributeType, inherit: false)
                .Cast<IRouteTemplateProvider>()
                .Single();
            return attr.Template!;
        }
    }
}
