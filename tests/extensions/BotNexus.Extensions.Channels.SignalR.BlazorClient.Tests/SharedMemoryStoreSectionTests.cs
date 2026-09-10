using System.Net;
using System.Net.Http;
using System.Text;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// The Memory page's shared-store section (#3232).
///
/// <remarks>
/// The per-agent table answers "who holds memory". This section answers "who can see whose",
/// which is the question that had no answer anywhere in the portal — a store several agents write
/// to is a channel one agent can use to influence what the others believe, and that relationship
/// lived only in config.json.
/// </remarks>
/// </summary>
public sealed class SharedMemoryStoreSectionTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public SharedMemoryStoreSectionTests() => _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

    public void Dispose() => _ctx.Dispose();

    /// <summary>Routes the two GETs the page makes, so neither has to be a live server.</summary>
    private sealed class StubHandler(string sharedJson, HttpStatusCode sharedStatus) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/api/memory/shared", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(sharedStatus)
                {
                    Content = new StringContent(sharedJson, Encoding.UTF8, "application/json")
                });
            }

            // The per-agent store list, which this suite does not exercise.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            });
        }
    }

    private IRenderedComponent<Pages.Memory> Render(
        string sharedJson = "[]",
        HttpStatusCode sharedStatus = HttpStatusCode.OK)
    {
        var http = new HttpClient(new StubHandler(sharedJson, sharedStatus))
        {
            BaseAddress = new Uri("http://localhost/")
        };
        _ctx.Services.AddSingleton(new MemoryApiClient(http));
        return _ctx.Render<Pages.Memory>();
    }

    // ── Nothing configured ────────────────────────────────────────────────

    [Fact]
    public void With_no_shared_stores_the_page_says_memory_is_private_and_where_to_change_that()
    {
        // The default, and the state of every gateway until someone configures one. An empty
        // section that just says "none" leaves the reader no better off.
        var cut = Render();

        var empty = cut.Find("[data-testid=memory-shared-empty]").TextContent;
        Assert.Contains("private", empty);
        Assert.Contains("Configuration", empty);
    }

    [Fact]
    public void A_gateway_without_the_endpoint_shows_the_empty_state_rather_than_an_error()
    {
        // A portal deployed ahead of its gateway gets a 404 here. That is "no shared stores", not
        // a failure worth blanking the page over.
        var cut = Render(sharedJson: "not json", sharedStatus: HttpStatusCode.NotFound);

        Assert.NotNull(cut.Find("[data-testid=memory-shared-empty]"));
    }

    // ── Configured ────────────────────────────────────────────────────────

    [Fact]
    public void A_wildcard_grant_is_shown_as_the_number_of_agents_it_actually_covers()
    {
        // "*" reads as harmless until you notice the roster has sixteen agents on it.
        var cut = Render("""
            [{"name":"platform","description":"What we learned","readers":["*"],"writers":["gantry-manager"],
              "readerCount":16,"writerCount":1}]
            """);

        Assert.Contains("All 16 agents", cut.Find("[data-testid=memory-shared-readers-platform]").TextContent);
        Assert.Contains("1 agent", cut.Find("[data-testid=memory-shared-writers-platform]").TextContent);
    }

    [Fact]
    public void A_store_every_agent_can_write_is_flagged()
    {
        // A store any agent can write is not shared knowledge, it is a shared surface - one agent
        // can leave a note the rest will read as fact. Readers being wide is ordinary; writers
        // being wide is the thing worth noticing.
        var cut = Render("""
            [{"name":"open","readers":["*"],"writers":["*"],"readerCount":3,"writerCount":3}]
            """);

        var writers = cut.Find("[data-testid=memory-shared-writers-open]");
        var readers = cut.Find("[data-testid=memory-shared-readers-open]");

        Assert.Contains("memory-badge-warn", writers.GetAttribute("class"));
        Assert.DoesNotContain("memory-badge-warn", readers.GetAttribute("class"));
    }

    [Fact]
    public void A_curated_store_is_not_flagged()
    {
        var cut = Render("""
            [{"name":"curated","readers":["*"],"writers":["gantry-manager"],"readerCount":3,"writerCount":1}]
            """);

        Assert.DoesNotContain(
            "memory-badge-warn",
            cut.Find("[data-testid=memory-shared-writers-curated]").GetAttribute("class"));
    }

    [Fact]
    public void An_empty_access_list_reads_as_nobody_rather_than_as_zero()
    {
        // "0 agents can write" invites the reading "the count has not loaded". Nobody is a state.
        var cut = Render("""
            [{"name":"frozen","readers":["*"],"writers":[],"readerCount":3,"writerCount":0}]
            """);

        Assert.Contains("Nobody", cut.Find("[data-testid=memory-shared-writers-frozen]").TextContent);
    }

    [Fact]
    public void The_configured_list_is_available_on_hover_behind_the_count()
    {
        // The count is what the grant means right now; the list is what was written down. A
        // roster change moves the count without anyone editing config.
        var cut = Render("""
            [{"name":"platform","readers":["gantry-manager","harbor-relay"],"writers":["gantry-manager"],
              "readerCount":2,"writerCount":1}]
            """);

        Assert.Equal(
            "gantry-manager, harbor-relay",
            cut.Find("[data-testid=memory-shared-readers-platform]").GetAttribute("title"));
    }

    [Fact]
    public void Retention_is_shown_when_a_store_has_one()
    {
        var cut = Render("""
            [{"name":"scratch","readers":["*"],"writers":["*"],"readerCount":1,"writerCount":1,"retentionDays":7}]
            """);

        Assert.Contains("kept 7 days", cut.Find("[data-testid=memory-shared-scratch]").TextContent);
    }
}
