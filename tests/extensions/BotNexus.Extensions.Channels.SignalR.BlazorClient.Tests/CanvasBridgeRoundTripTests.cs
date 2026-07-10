using System.Net;
using System.Reflection;
using System.Text.Json;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Gateway.Api.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Characterization tests for the iframe -> store -> agent seam (#1900).
///
/// The canvas iframe writes state via <c>window.canvasState.set(...)</c>, which posts a message that
/// the parent routes into <see cref="CanvasPanel.HandleCanvasMessage"/>. That handler makes an HTTP
/// call to <c>api/conversations/{id}/canvas-state/{key}</c>. Prior to #1900 the server route had been
/// accidentally token-expanded to <c>api/ConversationCanvas/...</c> by the #1732 extraction, so every
/// iframe write 404'd while the agent tool path (which writes to the store directly, bypassing HTTP)
/// kept working - which is exactly why <c>set_state</c>/<c>get_state</c> round-tripped in isolation but
/// the user's typed values never landed.
///
/// To exercise the real client&lt;-&gt;server seam these tests drive a mock <see cref="HttpMessageHandler"/>
/// whose routing is derived by reflection from the REAL route attributes on
/// <see cref="ConversationCanvasController"/>. If the composed server route does not match the URL the
/// component actually requests, the mock returns 404 and the round-trip fails - reproducing the bug.
/// </summary>
public sealed class CanvasBridgeRoundTripTests : IDisposable
{
    private const string ConversationId = "c_roundtrip";

    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store = new();
    private readonly RouteBackedCanvasStateHandler _handler = new();

    public CanvasBridgeRoundTripTests()
    {
        _store.SeedAgents([new AgentSummary("agent-1", "Alpha")]);
        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        _ctx.Services.AddScoped(_ => new HttpClient(_handler) { BaseAddress = new Uri("http://localhost/") });

        // Capture what the parent posts back to the iframe so we can assert the resolved value.
        _ctx.JSInterop.SetupVoid("canvasBridge.register", _ => true);
        _ctx.JSInterop.SetupVoid("canvasBridge.unregister", _ => true);
        _ctx.JSInterop.SetupVoid("canvasBridge.respond", _ => true);
    }

    public void Dispose() => _ctx.Dispose();

    /// <summary>
    /// Deliverable A: iframe set -> get round-trip through <see cref="CanvasPanel.HandleCanvasMessage"/>
    /// and the mock HttpClient. FAILS on pre-#1900 main (POST 404s against the token-expanded route so
    /// nothing is stored), passes once the controller route is pinned to <c>api/conversations</c>.
    /// </summary>
    [Fact]
    public async Task Iframe_set_then_get_round_trips_through_the_conversations_rest_route()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.CanvasHtml = "<html><body>canvas</body></html>";

        var cut = _ctx.Render<CanvasPanel>(parameters => parameters
            .Add(x => x.AgentId, "agent-1")
            .Add(x => x.ConversationId, ConversationId));

        // iframe: window.canvasState.set('f1', 'typed-value')
        var setJson = JsonSerializer.Serialize(new
        {
            type = "canvas-state-set",
            requestId = "req_1",
            key = "f1",
            value = "typed-value"
        });
        await cut.Instance.HandleCanvasMessage(setJson);

        // The write must actually land in the store via the REST route.
        _handler.Store.ContainsKey("f1").ShouldBe(true,
            "iframe canvasState.set must persist through the api/conversations/.../canvas-state route (#1900)");

        // iframe: window.canvasState.get('f1')
        var getJson = JsonSerializer.Serialize(new
        {
            type = "canvas-state-get",
            requestId = "req_2",
            key = "f1"
        });
        await cut.Instance.HandleCanvasMessage(getJson);

        _handler.Store["f1"].GetString().ShouldBe("typed-value");
    }

    /// <summary>
    /// Deliverable B: the URL the iframe write targets (composed from the component's request) must
    /// match the agent tool read scope for the SAME active conversation. Both must resolve to
    /// <c>api/conversations/{ConversationId}/canvas-state/...</c>. Guards candidate #1 (scope mismatch):
    /// the component is wired with <c>ConversationId=@Agent?.ActiveConversationId</c>, and the server
    /// route (read via reflection) must accept that exact conversation-scoped path.
    /// </summary>
    [Fact]
    public async Task Iframe_write_scope_matches_the_active_conversation_rest_route()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.CanvasHtml = "<html><body>canvas</body></html>";

        var cut = _ctx.Render<CanvasPanel>(parameters => parameters
            .Add(x => x.AgentId, "agent-1")
            .Add(x => x.ConversationId, ConversationId));

        var setJson = JsonSerializer.Serialize(new
        {
            type = "canvas-state-set",
            requestId = "req_scope",
            key = "updatedAt",
            value = "2025"
        });
        await cut.Instance.HandleCanvasMessage(setJson);

        // The handler only records a request path when the URL matched the real server route.
        _handler.LastMatchedPath.ShouldNotBeNull();
        _handler.LastMatchedPath.ShouldBe($"api/conversations/{ConversationId}/canvas-state/updatedAt");

        // And the composed server route template itself must be conversation-scoped (not token-expanded).
        RouteBackedCanvasStateHandler.SetKeyRouteTemplate
            .ShouldBe("api/conversations/{conversationId}/canvas-state/{key}");
    }

    /// <summary>
    /// A mock transport that mimics the gateway's canvas-state endpoints, but resolves incoming requests
    /// using the ACTUAL route attributes declared on <see cref="ConversationCanvasController"/>. This is
    /// what makes the test sensitive to the #1900 route regression: if the controller's composed route
    /// does not equal the URL the client requests, no endpoint matches and the handler returns 404.
    /// </summary>
    private sealed class RouteBackedCanvasStateHandler : HttpMessageHandler
    {
        public Dictionary<string, JsonElement> Store { get; } = new(StringComparer.Ordinal);
        public string? LastMatchedPath { get; private set; }

        public static string BaseRouteTemplate { get; } = ResolveBaseRoute();
        public static string SetKeyRouteTemplate { get; } =
            $"{BaseRouteTemplate}/{ResolveVerbTemplate(nameof(ConversationCanvasController.SetCanvasStateKey), typeof(HttpPostAttribute))}";
        public static string GetKeyRouteTemplate { get; } =
            $"{BaseRouteTemplate}/{ResolveVerbTemplate(nameof(ConversationCanvasController.GetCanvasStateKey), typeof(HttpGetAttribute))}";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');

            if (request.Method == HttpMethod.Post &&
                TryMatch(SetKeyRouteTemplate, path, out var setValues))
            {
                LastMatchedPath = path;
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(body);
                Store[setValues["key"]] = doc.RootElement.Clone();
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Get &&
                TryMatch(GetKeyRouteTemplate, path, out var getValues))
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

            // No server route matched the requested URL - this is exactly what happens on pre-#1900
            // main, where the controller route is api/ConversationCanvas/... but the client posts to
            // api/conversations/... .
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        // Matches a "seg/{token}/seg/{token}" template against a concrete path, extracting token values.
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
                {
                    values[t.Trim('{', '}')] = Uri.UnescapeDataString(pSegs[i]);
                }
                else if (!string.Equals(t, pSegs[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static string ResolveBaseRoute()
        {
            var attr = typeof(ConversationCanvasController)
                .GetCustomAttributes(typeof(RouteAttribute), inherit: false)
                .Cast<RouteAttribute>()
                .Single();

            // Emulate ASP.NET [controller] token replacement so this mock behaves like the real router.
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
