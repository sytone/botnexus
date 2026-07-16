using System.Net;
using System.Text.Json;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests for the Cron Jobs management page (nav section under Agents).
/// Verifies the list renders, empty state shows, and view/edit/delete controls
/// are present and wired to the <see cref="CronApiClient"/>.
/// </summary>
public sealed class CronJobsPageTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly CronJobsMockHandler _handler = new();

    public CronJobsPageTests()
    {
        var httpClient = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost/") };
        _ctx.Services.AddSingleton(httpClient);
        _ctx.Services.AddScoped<CronApiClient>();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Shows_empty_state_when_no_jobs()
    {
        _handler.SetupResponse("GET", "/api/cron", "[]");

        var cut = _ctx.Render<CronJobs>();
        cut.WaitForState(() => cut.Markup.Contains("No cron jobs configured"));

        Assert.Contains("No cron jobs configured", cut.Markup);
    }

    [Fact]
    public void Displays_jobs_in_table_with_action_buttons()
    {
        var jobs = JsonSerializer.Serialize(new[]
        {
            new
            {
                id = "job-1",
                name = "Nightly Report",
                schedule = "0 2 * * *",
                actionType = "agent-prompt",
                agentId = "farnsworth",
                enabled = true,
            }
        });
        _handler.SetupResponse("GET", "/api/cron", jobs);

        var cut = _ctx.Render<CronJobs>();
        cut.WaitForState(() => cut.Markup.Contains("Nightly Report"));

        Assert.Contains("Nightly Report", cut.Markup);
        Assert.Contains("0 2 * * *", cut.Markup);
        Assert.Contains("farnsworth", cut.Markup);
        // View (run now) + edit + delete controls are present.
        Assert.Contains("Run Nightly Report now", cut.Markup);
        Assert.Contains("Edit Nightly Report", cut.Markup);
        Assert.Contains("Delete Nightly Report", cut.Markup);
    }

    [Fact]
    public void Opens_edit_dialog_when_edit_clicked()
    {
        var jobs = JsonSerializer.Serialize(new[]
        {
            new { id = "job-1", name = "Nightly Report", schedule = "0 2 * * *", actionType = "agent-prompt", agentId = "farnsworth", enabled = true }
        });
        _handler.SetupResponse("GET", "/api/cron", jobs);

        var cut = _ctx.Render<CronJobs>();
        cut.WaitForState(() => cut.Markup.Contains("Nightly Report"));

        cut.Find("[aria-label='Edit Nightly Report']").Click();

        Assert.Contains("cron-edit-dialog", cut.Markup);
        Assert.Contains("Edit Cron Job", cut.Markup);
    }

    [Fact]
    public void Opens_delete_confirmation_when_delete_clicked()
    {
        var jobs = JsonSerializer.Serialize(new[]
        {
            new { id = "job-1", name = "Nightly Report", schedule = "0 2 * * *", actionType = "agent-prompt", agentId = "farnsworth", enabled = true }
        });
        _handler.SetupResponse("GET", "/api/cron", jobs);

        var cut = _ctx.Render<CronJobs>();
        cut.WaitForState(() => cut.Markup.Contains("Nightly Report"));

        cut.Find("[aria-label='Delete Nightly Report']").Click();

        Assert.Contains("Delete Cron Job", cut.Markup);
        Assert.Contains("This cannot be undone", cut.Markup);
    }

    [Fact]
    public void Opens_detail_modal_with_execution_history_when_view_clicked()
    {
        var jobs = JsonSerializer.Serialize(new[]
        {
            new { id = "job-1", name = "Nightly Report", schedule = "0 2 * * *", actionType = "agent-prompt", agentId = "farnsworth", enabled = true }
        });
        _handler.SetupResponse("GET", "/api/cron", jobs);
        var runs = JsonSerializer.Serialize(new[]
        {
            new { id = "run-1", jobId = "job-1", startedAt = "2026-07-16T02:00:00Z", completedAt = "2026-07-16T02:00:05Z", status = "ok", sessionId = "cron:job-1" }
        });
        _handler.SetupResponse("GET", "/api/cron/job-1/runs", runs);

        var cut = _ctx.Render<CronJobs>();
        cut.WaitForState(() => cut.Markup.Contains("Nightly Report"));

        cut.Find("[aria-label='View Nightly Report details']").Click();
        cut.WaitForState(() => cut.Markup.Contains("cron-detail-modal"));

        Assert.Contains("cron-detail-modal", cut.Markup);
        Assert.Contains("Execution History", cut.Markup);
        // Run row renders from the /runs endpoint.
        cut.WaitForState(() => cut.Markup.Contains("cron-runs-table"));
        Assert.Contains("cron-runs-table", cut.Markup);
    }

    [Fact]
    public void Edit_dialog_shows_provider_and_model_dropdowns_for_agent_prompt()
    {
        var jobs = JsonSerializer.Serialize(new[]
        {
            new { id = "job-1", name = "Nightly Report", schedule = "0 2 * * *", actionType = "agent-prompt", agentId = "farnsworth", enabled = true }
        });
        _handler.SetupResponse("GET", "/api/cron", jobs);
        _handler.SetupResponse("GET", "/api/providers", "[]");

        var cut = _ctx.Render<CronJobs>();
        cut.WaitForState(() => cut.Markup.Contains("Nightly Report"));

        cut.Find("[aria-label='Edit Nightly Report']").Click();
        cut.WaitForState(() => cut.Markup.Contains("cron-edit-dialog"));

        // Provider + model selectors are present for agent-prompt jobs.
        Assert.Contains("cron-edit-provider", cut.Markup);
        Assert.Contains("cron-edit-model", cut.Markup);
    }

    private sealed class CronJobsMockHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, HttpResponseMessage> _responses = new(StringComparer.OrdinalIgnoreCase);

        public void SetupResponse(string method, string pathSuffix, string jsonContent)
        {
            _responses[$"{method}:{pathSuffix}"] = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json")
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            var methodKey = $"{request.Method.Method}:{path}";

            // Match the most specific (longest) configured key first so that, e.g.,
            // "/api/cron/job-1/runs" is not shadowed by the "/api/cron" list stub.
            foreach (var (key, response) in _responses.OrderByDescending(kv => kv.Key.Length))
            {
                if (methodKey.Contains(key, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
