using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests the portal tools management UI at <c>/tools</c> (#2235, slice 4 of #2231): the add / edit
/// / remove form interactions, the required-name and absolute-URL validation, and the fact that a
/// successful mutation raises <see cref="ToolsApiClient.Changed"/> so the Tools nav section
/// repaints without a page reload.
/// </summary>
public sealed class ToolsManagePageTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ToolsManageMockHandler _handler = new();

    public ToolsManagePageTests()
    {
        var httpClient = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost/") };
        _ctx.Services.AddSingleton(httpClient);
        _ctx.Services.AddScoped<ToolsApiClient>();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Empty_state_renders_when_no_tools_configured()
    {
        SetupList();

        var cut = _ctx.Render<ToolsManage>();
        cut.WaitForState(() => cut.Markup.Contains("tools-manage-empty"));

        Assert.Contains("No tools configured yet", cut.Markup);
        Assert.Empty(cut.FindAll("[data-testid='tools-list-item']"));
    }

    [Fact]
    public void Existing_tools_render_with_order_and_sandbox_state()
    {
        SetupList(
            Tool("a", "Grafana", "https://grafana.example/", order: 0, sandbox: true),
            Tool("b", "Wiki", "https://wiki.example/", order: 1, sandbox: false));

        var cut = _ctx.Render<ToolsManage>();
        cut.WaitForState(() => cut.FindAll("[data-testid='tools-list-item']").Count == 2);

        var items = cut.FindAll("[data-testid='tools-list-item']");
        Assert.Contains("Grafana", items[0].TextContent);
        Assert.Contains("Sandboxed", items[0].TextContent);
        Assert.Contains("Wiki", items[1].TextContent);
        Assert.Contains("Unsandboxed", items[1].TextContent);
    }

    [Fact]
    public void Add_form_requires_a_name()
    {
        SetupList();

        var cut = _ctx.Render<ToolsManage>();
        cut.WaitForState(() => cut.Markup.Contains("tools-manage-empty"));
        cut.Find("[data-testid='tools-add-btn']").Click();

        // URL only, no name.
        cut.Find("[data-testid='tools-form-url']").Input("https://example.com/");
        cut.Find("[data-testid='tools-form-save']").Click();

        Assert.Contains("Name is required.", cut.Find("[data-testid='tools-form-errors']").TextContent);
        // Nothing may be sent to the server while the form is invalid.
        Assert.DoesNotContain(_handler.Requests, r => r.StartsWith("POST", StringComparison.Ordinal));
    }

    [Fact]
    public void Add_form_rejects_a_non_absolute_url()
    {
        SetupList();

        var cut = _ctx.Render<ToolsManage>();
        cut.WaitForState(() => cut.Markup.Contains("tools-manage-empty"));
        cut.Find("[data-testid='tools-add-btn']").Click();

        cut.Find("[data-testid='tools-form-name']").Input("Bad");
        cut.Find("[data-testid='tools-form-url']").Input("/relative/path");
        cut.Find("[data-testid='tools-form-save']").Click();

        Assert.Contains("absolute http", cut.Find("[data-testid='tools-form-errors']").TextContent);
        Assert.DoesNotContain(_handler.Requests, r => r.StartsWith("POST", StringComparison.Ordinal));
    }

    [Fact]
    public void Add_form_rejects_a_javascript_url()
    {
        SetupList();

        var cut = _ctx.Render<ToolsManage>();
        cut.WaitForState(() => cut.Markup.Contains("tools-manage-empty"));
        cut.Find("[data-testid='tools-add-btn']").Click();

        cut.Find("[data-testid='tools-form-name']").Input("Evil");
        // The URL becomes an iframe src on the host route, so a javascript: scheme must not pass.
        cut.Find("[data-testid='tools-form-url']").Input("javascript:alert(1)");
        cut.Find("[data-testid='tools-form-save']").Click();

        Assert.Contains("absolute http", cut.Find("[data-testid='tools-form-errors']").TextContent);
        Assert.DoesNotContain(_handler.Requests, r => r.StartsWith("POST", StringComparison.Ordinal));
    }

    [Fact]
    public void Adding_a_valid_tool_posts_all_fields_and_notifies_the_nav()
    {
        SetupList();
        _handler.SetupJson("POST", "/api/tools", HttpStatusCode.Created,
            JsonSerializer.Serialize(Tool("new", "Metrics", "https://metrics.example/", 3, true)));

        var changed = 0;
        _ctx.Services.GetRequiredService<ToolsApiClient>().Changed += () => changed++;

        var cut = _ctx.Render<ToolsManage>();
        cut.WaitForState(() => cut.Markup.Contains("tools-manage-empty"));
        cut.Find("[data-testid='tools-add-btn']").Click();

        cut.Find("[data-testid='tools-form-name']").Input("Metrics");
        cut.Find("[data-testid='tools-form-url']").Input("https://metrics.example/");
        cut.Find("[data-testid='tools-form-icon']").Input("\U0001F4C8");
        cut.Find("[data-testid='tools-form-order']").Input("3");
        cut.Find("[data-testid='tools-form-sandbox']").Change(false);
        cut.Find("[data-testid='tools-form-save']").Click();

        cut.WaitForState(() => _handler.LastPostBody is not null);

        var body = _handler.LastPostBody!;
        Assert.Contains("\"name\":\"Metrics\"", body);
        Assert.Contains("\"url\":\"https://metrics.example/\"", body);
        Assert.Contains("\"order\":3", body);
        Assert.Contains("\"sandboxEnabled\":false", body);
        // The nav must learn about the new tool without a page reload.
        cut.WaitForState(() => changed == 1);
    }

    [Fact]
    public void Editing_a_tool_prefills_the_form_and_puts_to_the_same_id()
    {
        SetupList(Tool("grafana", "Grafana", "https://grafana.example/", 0, true));
        _handler.SetupJson("PUT", "/api/tools/grafana", HttpStatusCode.OK,
            JsonSerializer.Serialize(Tool("grafana", "Grafana Prod", "https://grafana.example/", 0, true)));

        var cut = _ctx.Render<ToolsManage>();
        cut.WaitForState(() => cut.FindAll("[data-testid='tools-list-item']").Count == 1);
        cut.Find("[data-testid='tools-edit-btn']").Click();

        // Existing values are prefilled so an edit is a change, not a re-entry.
        Assert.Equal("Grafana", cut.Find("[data-testid='tools-form-name']").GetAttribute("value"));
        Assert.Equal("https://grafana.example/", cut.Find("[data-testid='tools-form-url']").GetAttribute("value"));

        cut.Find("[data-testid='tools-form-name']").Input("Grafana Prod");
        cut.Find("[data-testid='tools-form-save']").Click();

        cut.WaitForState(() => _handler.Requests.Any(r => r == "PUT /api/tools/grafana"));
        Assert.Contains("\"name\":\"Grafana Prod\"", _handler.LastPutBody!);
    }

    [Fact]
    public void Cancelling_an_edit_sends_nothing_and_closes_the_form()
    {
        SetupList(Tool("grafana", "Grafana", "https://grafana.example/", 0, true));

        var cut = _ctx.Render<ToolsManage>();
        cut.WaitForState(() => cut.FindAll("[data-testid='tools-list-item']").Count == 1);
        cut.Find("[data-testid='tools-edit-btn']").Click();
        cut.Find("[data-testid='tools-form-name']").Input("Discarded");
        cut.Find("[data-testid='tools-form-cancel']").Click();

        Assert.Empty(cut.FindAll("[data-testid='tools-editor']"));
        Assert.DoesNotContain(_handler.Requests, r => r.StartsWith("PUT", StringComparison.Ordinal));
        // The list still shows the original name - the edit was on a copy.
        Assert.Contains("Grafana", cut.Find("[data-testid='tools-list-item']").TextContent);
    }

    [Fact]
    public void Remove_requires_confirmation_before_deleting()
    {
        SetupList(Tool("grafana", "Grafana", "https://grafana.example/", 0, true));

        var cut = _ctx.Render<ToolsManage>();
        cut.WaitForState(() => cut.FindAll("[data-testid='tools-list-item']").Count == 1);
        cut.Find("[data-testid='tools-remove-btn']").Click();

        // Confirmation dialog appears; nothing has been deleted yet.
        Assert.NotEmpty(cut.FindAll("[data-testid='tools-delete-confirm']"));
        Assert.DoesNotContain(_handler.Requests, r => r.StartsWith("DELETE", StringComparison.Ordinal));

        cut.Find("[data-testid='tools-delete-cancel-btn']").Click();
        Assert.Empty(cut.FindAll("[data-testid='tools-delete-confirm']"));
        Assert.DoesNotContain(_handler.Requests, r => r.StartsWith("DELETE", StringComparison.Ordinal));
    }

    [Fact]
    public void Confirming_remove_deletes_the_tool_and_notifies_the_nav()
    {
        SetupList(Tool("grafana", "Grafana", "https://grafana.example/", 0, true));
        _handler.SetupJson("DELETE", "/api/tools/grafana", HttpStatusCode.NoContent, "");

        var changed = 0;
        _ctx.Services.GetRequiredService<ToolsApiClient>().Changed += () => changed++;

        var cut = _ctx.Render<ToolsManage>();
        cut.WaitForState(() => cut.FindAll("[data-testid='tools-list-item']").Count == 1);
        cut.Find("[data-testid='tools-remove-btn']").Click();
        cut.Find("[data-testid='tools-delete-confirm-btn']").Click();

        cut.WaitForState(() => _handler.Requests.Any(r => r == "DELETE /api/tools/grafana"));
        cut.WaitForState(() => changed == 1);
    }

    [Fact]
    public void Failed_save_surfaces_an_error_and_keeps_the_form_open()
    {
        SetupList();
        _handler.SetupJson("POST", "/api/tools", HttpStatusCode.InternalServerError, "{}");

        var cut = _ctx.Render<ToolsManage>();
        cut.WaitForState(() => cut.Markup.Contains("tools-manage-empty"));
        cut.Find("[data-testid='tools-add-btn']").Click();
        cut.Find("[data-testid='tools-form-name']").Input("Broken");
        cut.Find("[data-testid='tools-form-url']").Input("https://broken.example/");
        cut.Find("[data-testid='tools-form-save']").Click();

        cut.WaitForState(() => cut.Markup.Contains("Could not create the tool."));
        // The user's input must not be thrown away when the server rejects it.
        Assert.NotEmpty(cut.FindAll("[data-testid='tools-editor']"));
    }

    private void SetupList(params object[] tools) =>
        _handler.SetupJson("GET", "/api/tools", HttpStatusCode.OK, JsonSerializer.Serialize(tools));

    private static object Tool(string id, string name, string url, int order, bool sandbox) => new
    {
        id,
        name,
        url,
        icon = "",
        order,
        sandboxEnabled = sandbox
    };

    /// <summary>
    /// Records every request method+path so the tests can assert that an invalid form never reaches
    /// the server, and replays canned responses keyed on method + exact path.
    /// </summary>
    private sealed class ToolsManageMockHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Json)> _responses =
            new(StringComparer.OrdinalIgnoreCase);

        public List<string> Requests { get; } = [];
        public string? LastPostBody { get; private set; }
        public string? LastPutBody { get; private set; }

        public void SetupJson(string method, string path, HttpStatusCode status, string json) =>
            _responses[$"{method} {path}"] = (status, json);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            var key = $"{request.Method.Method} {path}";
            Requests.Add(key);

            if (request.Content is not null)
            {
                var body = await request.Content.ReadAsStringAsync(cancellationToken);
                if (request.Method == HttpMethod.Post)
                    LastPostBody = body;
                else if (request.Method == HttpMethod.Put)
                    LastPutBody = body;
            }

            if (_responses.TryGetValue(key, out var canned))
            {
                return new HttpResponseMessage(canned.Status)
                {
                    Content = new StringContent(canned.Json, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }
    }
}
