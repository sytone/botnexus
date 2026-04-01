using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BotNexus.Tests.E2E.Infrastructure;
using FluentAssertions;

namespace BotNexus.Tests.E2E.Tests;

/// <summary>
/// E2E tests for the cron system covering the full lifecycle:
/// config → startup → scheduled execution → channel output.
/// Scenarios SC-CRN-001 through SC-CRN-008.
/// </summary>
[Collection(CronE2eCollection.Name)]
public sealed class CronTests(CronFixture fixture) : IAsyncLifetime
{
    // -- SC-CRN-001 ----------------------------------------------------------

    /// <summary>
    /// Gateway starts with cron jobs configured → jobs registered and visible
    /// via GET /api/cron. Validates all 5 expected jobs (3 central + 1 toggle
    /// + 1 legacy-migrated) appear with correct attributes.
    /// </summary>
    [Fact]
    public async Task Jobs_RegisteredAtStartup_VisibleViaApi()
    {
        var response = await fixture.Client.GetAsync("/api/cron");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var jobs = await response.Content.ReadFromJsonAsync<JsonElement>();
        var jobNames = jobs.EnumerateArray()
            .Select(j => j.GetProperty("name").GetString())
            .ToList();

        jobNames.Should().Contain("nova-briefing", because: "agent cron job should be registered");
        jobNames.Should().Contain("system:health-audit", because: "system cron job should be registered");
        jobNames.Should().Contain("maintenance:consolidate-memory", because: "maintenance cron job should be registered");
        jobNames.Should().Contain("system:check-updates", because: "toggle-test cron job should be registered");
        jobNames.Should().Contain("echo", because: "legacy AgentConfig.CronJobs should be migrated");

        // Verify a representative job has correct schedule and enabled flag
        var novaBriefing = jobs.EnumerateArray()
            .First(j => j.GetProperty("name").GetString() == "nova-briefing");
        novaBriefing.GetProperty("schedule").GetString().Should().Be("0 0 1 1 *");
        novaBriefing.GetProperty("enabled").GetBoolean().Should().BeTrue();
    }

    // -- SC-CRN-002 ----------------------------------------------------------

    /// <summary>
    /// Agent cron job fires → prompt sent through agent → response routed to
    /// mock channel. Validates the full pipeline: CronService trigger → AgentRunner
    /// → MockLlmProvider → session history → OutputChannel routing.
    /// </summary>
    [Fact]
    public async Task AgentJob_WhenTriggered_RoutesResponseToMockChannel()
    {
        fixture.WebChannel.Reset();

        var triggerResponse = await fixture.Client.PostAsync(
            "/api/cron/nova-briefing/trigger", null);
        triggerResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Wait for the mock channel to receive the routed response
        var channelMessage = await fixture.WebChannel.WaitForResponseAsync(
            chatId: "cron:nova-briefing", timeout: TimeSpan.FromSeconds(15));

        channelMessage.Content.Should().NotBeNullOrEmpty(
            because: "agent cron job should produce a response and route it to the output channel");
        channelMessage.Channel.Should().Be("mock-web");
        channelMessage.Metadata.Should().ContainKey("source");
        channelMessage.Metadata["source"].Should().Be("cron");

        // Verify the execution was also recorded in history with success
        var jobDetail = await WaitForJobHistoryAsync("nova-briefing");
        var history = jobDetail.GetProperty("history");
        history.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);
        var execution = history.EnumerateArray().First();
        execution.GetProperty("success").GetBoolean().Should().BeTrue();
        execution.GetProperty("output").GetString().Should().NotBeNullOrEmpty(
            because: "the agent's response should be captured in execution history");
    }

    // -- SC-CRN-003 ----------------------------------------------------------

    /// <summary>
    /// System cron job fires → action executed → result recorded in history.
    /// The health-audit action runs HealthCheckService and reports status.
    /// </summary>
    [Fact]
    public async Task SystemJob_WhenTriggered_ExecutesActionAndRecordsHistory()
    {
        var triggerResponse = await fixture.Client.PostAsync(
            "/api/cron/system:health-audit/trigger", null);
        triggerResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var jobDetail = await WaitForJobHistoryAsync("system:health-audit");

        var history = jobDetail.GetProperty("history");
        history.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);

        var execution = history.EnumerateArray().First();
        execution.GetProperty("success").GetBoolean().Should().BeTrue();
        execution.GetProperty("output").GetString().Should().Contain("health-audit",
            because: "HealthAuditAction output should reference the action name");
    }

    // -- SC-CRN-004 ----------------------------------------------------------

    /// <summary>
    /// Maintenance cron job fires → memory consolidation triggered.
    /// Verifies the MockMemoryConsolidator was called for the configured agent
    /// and the result is recorded in execution history.
    /// </summary>
    [Fact]
    public async Task MaintenanceJob_WhenTriggered_ConsolidatesMemory()
    {
        var triggerResponse = await fixture.Client.PostAsync(
            "/api/cron/maintenance:consolidate-memory/trigger", null);
        triggerResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var jobDetail = await WaitForJobHistoryAsync("maintenance:consolidate-memory");

        var history = jobDetail.GetProperty("history");
        history.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);

        var execution = history.EnumerateArray().First();
        execution.GetProperty("success").GetBoolean().Should().BeTrue();
        execution.GetProperty("output").GetString().Should().Contain("nova",
            because: "consolidation output should reference the processed agent");

        fixture.MemoryConsolidator.ConsolidatedAgents.Should().Contain("nova",
            because: "MockMemoryConsolidator should have been called for the 'nova' agent");
    }

    // -- SC-CRN-005 ----------------------------------------------------------

    /// <summary>
    /// Manual trigger via POST /api/cron/{name}/trigger → job executes
    /// immediately. Validates trigger response shape and that execution
    /// appears in history.
    /// </summary>
    [Fact]
    public async Task ManualTrigger_ExecutesImmediately_AppearsInHistory()
    {
        var triggerResponse = await fixture.Client.PostAsync(
            "/api/cron/system:check-updates/trigger", null);
        triggerResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await triggerResponse.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("triggered").GetBoolean().Should().BeTrue();
        body.GetProperty("jobName").GetString().Should().Be("system:check-updates");

        // Verify execution recorded
        var jobDetail = await WaitForJobHistoryAsync("system:check-updates");
        var history = jobDetail.GetProperty("history");
        history.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);
    }

    // -- SC-CRN-006 ----------------------------------------------------------

    /// <summary>
    /// Enable/disable via PUT /api/cron/{name}/enable → disabled jobs
    /// reflect correct state. Tests the full cycle: disable → verify →
    /// re-enable → verify.
    /// </summary>
    [Fact]
    public async Task EnableDisable_ChangesJobStateViaApi()
    {
        // Disable the toggle-test job
        var disableResponse = await fixture.Client.PutAsync(
            "/api/cron/system:check-updates/enable",
            JsonContent.Create(new { enabled = false }));
        disableResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var disableBody = await disableResponse.Content.ReadFromJsonAsync<JsonElement>();
        disableBody.GetProperty("enabled").GetBoolean().Should().BeFalse();
        disableBody.GetProperty("jobName").GetString().Should().Be("system:check-updates");

        // Verify via GET that the job shows as disabled
        var getResponse = await fixture.Client.GetAsync("/api/cron/system:check-updates");
        var jobDetail = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        jobDetail.GetProperty("enabled").GetBoolean().Should().BeFalse();

        // Re-enable
        var enableResponse = await fixture.Client.PutAsync(
            "/api/cron/system:check-updates/enable",
            JsonContent.Create(new { enabled = true }));
        enableResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify re-enabled
        var getResponse2 = await fixture.Client.GetAsync("/api/cron/system:check-updates");
        var jobDetail2 = await getResponse2.Content.ReadFromJsonAsync<JsonElement>();
        jobDetail2.GetProperty("enabled").GetBoolean().Should().BeTrue();
    }

    // -- SC-CRN-007 ----------------------------------------------------------

    /// <summary>
    /// GET /api/cron/history returns execution records with correct data
    /// fields: jobName, correlationId, startedAt, completedAt, success.
    /// </summary>
    [Fact]
    public async Task History_ReturnsExecutionRecordsWithCorrectFields()
    {
        // Trigger a job to guarantee at least one history entry
        await fixture.Client.PostAsync("/api/cron/system:health-audit/trigger", null);
        await WaitForJobHistoryAsync("system:health-audit");

        var historyResponse = await fixture.Client.GetAsync("/api/cron/history?limit=50");
        historyResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var history = await historyResponse.Content.ReadFromJsonAsync<JsonElement>();
        var entries = history.EnumerateArray().ToList();
        entries.Should().NotBeEmpty();

        // Find an entry for the health-audit job and validate all fields
        var entry = entries.First(e =>
            e.GetProperty("jobName").GetString() == "system:health-audit");

        entry.GetProperty("correlationId").GetString().Should().NotBeNullOrEmpty(
            because: "each execution should have a unique correlation ID");
        entry.GetProperty("startedAt").GetString().Should().NotBeNullOrEmpty();
        entry.GetProperty("completedAt").GetString().Should().NotBeNullOrEmpty();
        entry.TryGetProperty("success", out var successProp).Should().BeTrue();
        successProp.GetBoolean().Should().BeTrue();
    }

    // -- SC-CRN-008 ----------------------------------------------------------

    /// <summary>
    /// Legacy AgentConfig.CronJobs migration → old config converted to central
    /// jobs. The echo agent's deprecated CronJobs list should be migrated to
    /// the central cron registry and visible via the API.
    /// </summary>
    [Fact]
    public async Task LegacyConfig_MigratedToCentralJobs()
    {
        var response = await fixture.Client.GetAsync("/api/cron");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var jobs = await response.Content.ReadFromJsonAsync<JsonElement>();

        // The legacy echo cron job should appear with the agent name as the job name
        var echoJob = jobs.EnumerateArray()
            .FirstOrDefault(j => j.GetProperty("name").GetString() == "echo");

        echoJob.ValueKind.Should().NotBe(JsonValueKind.Undefined,
            because: "legacy AgentConfig.CronJobs for echo should be migrated to central cron config");
        echoJob.GetProperty("schedule").GetString().Should().Be("0 0 1 1 *");
        echoJob.GetProperty("enabled").GetBoolean().Should().BeTrue();
    }

    // -- Helpers --------------------------------------------------------------

    /// <summary>
    /// Polls GET /api/cron/{name} until the expected number of history entries
    /// appear, with a configurable timeout.
    /// </summary>
    private async Task<JsonElement> WaitForJobHistoryAsync(
        string jobName, int expectedCount = 1, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        JsonElement lastDetail = default;

        while (DateTime.UtcNow < deadline)
        {
            var response = await fixture.Client.GetAsync($"/api/cron/{jobName}");
            if (response.StatusCode == HttpStatusCode.OK)
            {
                lastDetail = await response.Content.ReadFromJsonAsync<JsonElement>();
                if (lastDetail.TryGetProperty("history", out var history) &&
                    history.GetArrayLength() >= expectedCount)
                {
                    return lastDetail;
                }
            }

            await Task.Delay(200);
        }

        throw new TimeoutException(
            $"Expected {expectedCount} history entries for job '{jobName}' within {(timeout ?? TimeSpan.FromSeconds(15)).TotalSeconds}s. " +
            $"Last response: {lastDetail}");
    }

    public Task InitializeAsync()
    {
        fixture.WebChannel.Reset();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
