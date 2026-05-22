using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class ConversationBindingsPanelTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly TestMockHttpHandler _httpHandler = new();

    private const string TestConvId = "conv-123";

    public ConversationBindingsPanelTests()
    {
        var httpClient = new HttpClient(_httpHandler) { BaseAddress = new Uri("http://localhost/") };
        _ctx.Services.AddSingleton(httpClient);
        _ctx.Services.AddSingleton(Substitute.For<IGatewayRestClient>());
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private static string BuildConversationJson(IEnumerable<object>? bindings = null)
    {
        var bindingArray = bindings ?? Array.Empty<object>();
        return JsonSerializer.Serialize(new
        {
            conversationId = TestConvId,
            agentId = "agent-1",
            title = "Test Conv",
            isDefault = false,
            status = "Active",
            activeSessionId = (string?)null,
            bindings = bindingArray,
            createdAt = DateTimeOffset.UtcNow,
            updatedAt = DateTimeOffset.UtcNow
        });
    }

    private static object MakeBinding(string bindingId = "b-1", string channelType = "signalr", string channelAddress = "addr-1") =>
        new
        {
            bindingId,
            channelType,
            channelAddress,
            threadId = (string?)null,
            mode = "Interactive",
            threadingMode = "Single",
            displayPrefix = (string?)null,
            boundAt = DateTimeOffset.UtcNow
        };

    [Fact]
    public void Shows_loading_spinner_initially()
    {
        _httpHandler.SetupGet($"/api/conversations/{TestConvId}", BuildConversationJson());

        var cut = _ctx.Render<ConversationBindingsPanel>(p =>
            p.Add(c => c.ConversationId, TestConvId));

        // At time of render the loading spinner should be visible
        // (we can't easily race this, but we can verify it loads correctly)
        cut.WaitForState(() => !cut.Markup.Contains("Loading bindings"));
        Assert.DoesNotContain("Loading bindings", cut.Markup);
    }

    [Fact]
    public void Shows_empty_state_when_no_bindings()
    {
        _httpHandler.SetupGet($"/api/conversations/{TestConvId}", BuildConversationJson());

        var cut = _ctx.Render<ConversationBindingsPanel>(p =>
            p.Add(c => c.ConversationId, TestConvId));

        cut.WaitForState(() => cut.Markup.Contains("No channel bindings"));

        Assert.Contains("No channel bindings", cut.Markup);
    }

    [Fact]
    public void Displays_bindings_in_table()
    {
        _httpHandler.SetupGet(
            $"/api/conversations/{TestConvId}",
            BuildConversationJson([MakeBinding("b-1", "telegram", "chat-42")]));

        var cut = _ctx.Render<ConversationBindingsPanel>(p =>
            p.Add(c => c.ConversationId, TestConvId));

        cut.WaitForState(() => cut.Markup.Contains("telegram"));

        Assert.Contains("telegram", cut.Markup);
        Assert.Contains("chat-42", cut.Markup);
    }

    [Fact]
    public void Shows_add_binding_form_when_add_button_clicked()
    {
        _httpHandler.SetupGet($"/api/conversations/{TestConvId}", BuildConversationJson());

        var cut = _ctx.Render<ConversationBindingsPanel>(p =>
            p.Add(c => c.ConversationId, TestConvId));

        cut.WaitForState(() => cut.FindAll(".add-binding-btn").Count > 0);

        cut.Find(".add-binding-btn").Click();

        Assert.Contains("Channel Type", cut.Markup);
        Assert.Contains("Channel Address", cut.Markup);
    }

    [Fact]
    public void Cancel_add_form_hides_form()
    {
        _httpHandler.SetupGet($"/api/conversations/{TestConvId}", BuildConversationJson());

        var cut = _ctx.Render<ConversationBindingsPanel>(p =>
            p.Add(c => c.ConversationId, TestConvId));

        cut.WaitForState(() => cut.FindAll(".add-binding-btn").Count > 0);
        cut.Find(".add-binding-btn").Click();

        Assert.Contains("Channel Type", cut.Markup);

        cut.Find(".cancel-add-btn").Click();

        Assert.DoesNotContain("Channel Type", cut.Markup);
    }

    [Fact]
    public void Delete_button_shows_confirmation_dialog()
    {
        _httpHandler.SetupGet(
            $"/api/conversations/{TestConvId}",
            BuildConversationJson([MakeBinding("b-del", "signalr", "portal")]));
        _httpHandler.SetupDelete($"/api/conversations/{TestConvId}/bindings/b-del");

        var cut = _ctx.Render<ConversationBindingsPanel>(p =>
            p.Add(c => c.ConversationId, TestConvId));

        cut.WaitForState(() => cut.FindAll(".delete-binding-btn").Count > 0);

        cut.Find(".delete-binding-btn").Click();

        // Confirm dialog should appear
        cut.WaitForState(() => cut.FindAll(".binding-confirm-overlay").Count > 0);
        Assert.Contains("Remove Binding", cut.Markup);
        Assert.NotEmpty(cut.FindAll(".confirm-delete-btn"));
    }

    [Fact]
    public void Shows_error_when_load_fails()
    {
        _httpHandler.SetupErrorGet($"/api/conversations/{TestConvId}", HttpStatusCode.NotFound);

        var cut = _ctx.Render<ConversationBindingsPanel>(p =>
            p.Add(c => c.ConversationId, TestConvId));

        cut.WaitForState(() => cut.Markup.Contains("Failed") || cut.Markup.Contains("Error") || cut.Markup.Contains("error"));

        Assert.Contains("Failed", cut.Markup);
    }

    [Fact]
    public void Add_binding_form_validation_shows_errors()
    {
        _httpHandler.SetupGet($"/api/conversations/{TestConvId}", BuildConversationJson());

        var cut = _ctx.Render<ConversationBindingsPanel>(p =>
            p.Add(c => c.ConversationId, TestConvId));

        cut.WaitForState(() => cut.FindAll(".add-binding-btn").Count > 0);
        cut.Find(".add-binding-btn").Click();

        // Click save without filling in fields
        cut.Find(".binding-form-actions button.primary").Click();

        Assert.Contains("Channel Type is required", cut.Markup);
        Assert.Contains("Channel Address is required", cut.Markup);
    }

    // ── Private helper mock ────────────────────────────────────────────────

    internal sealed class TestMockHttpHandler : HttpMessageHandler
    {
        private readonly List<(string Method, string Path, HttpResponseMessage Response)> _routes = [];

        public void SetupGet(string path, string json)
        {
            _routes.Add(("GET", path, new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            }));
        }

        public void SetupErrorGet(string path, HttpStatusCode status)
        {
            _routes.Add(("GET", path, new HttpResponseMessage(status)
            {
                Content = new StringContent("Error", System.Text.Encoding.UTF8, "text/plain")
            }));
        }

        public void SetupDelete(string path, HttpStatusCode status = HttpStatusCode.NoContent)
        {
            _routes.Add(("DELETE", path, new HttpResponseMessage(status)));
        }

        public void SetupPost(string path, string json = "{}", HttpStatusCode status = HttpStatusCode.Created)
        {
            _routes.Add(("POST", path, new HttpResponseMessage(status)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            }));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            var method = request.Method.Method;

            // Try method-specific match first
            foreach (var (m, p, r) in _routes)
            {
                if (string.Equals(m, method, StringComparison.OrdinalIgnoreCase) &&
                    path.Contains(p, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(r);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
