using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using BotNexus.Core.Models;
using BotNexus.Gateway;
using BotNexus.Providers.Base;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BotNexus.Tests.Integration.Tests;

[CollectionDefinition("cron-integration", DisableParallelization = true)]
public sealed class CronIntegrationCollection;

[Collection("cron-integration")]
public sealed class CronIntegrationTests
{
    [Fact]
    public async Task GatewayStart_WithCronConfig_HasRunningCronServiceAndRegisteredJobs()
    {
        using var scope = new TestScope();
        using var factory = CreateFactory(
            scope,
            CronAgentJob("agent-digest", "agent digest prompt"));

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/cron");
        response.EnsureSuccessStatusCode();
        var jobs = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        jobs.EnumerateArray().Select(static e => e.GetProperty("name").GetString())
            .Should()
            .Contain("agent-digest");

        using var diScope = factory.Services.CreateScope();
        diScope.ServiceProvider.GetRequiredService<ICronService>().IsRunning.Should().BeTrue();
    }

    [Fact]
    public async Task AgentCronJob_Trigger_ExecutesAgentRunnerPipelineAndCapturesResponse()
    {
        using var scope = new TestScope();
        using var factory = CreateFactory(
            scope,
            CronAgentJob("agent-pipeline", "pipeline prompt"));
        using var client = factory.CreateClient();

        var triggerResponse = await client.PostAsync("/api/cron/agent-pipeline/trigger", content: null);
        triggerResponse.EnsureSuccessStatusCode();

        using var diScope = factory.Services.CreateScope();
        var cronService = diScope.ServiceProvider.GetRequiredService<ICronService>();
        var sessionManager = diScope.ServiceProvider.GetRequiredService<ISessionManager>();

        await WaitForAsync(() => cronService.GetHistory("agent-pipeline").Count > 0);

        var execution = cronService.GetHistory("agent-pipeline").Single();
        execution.Success.Should().BeTrue();
        execution.Output.Should().StartWith("provider-response:");

        var sessionKey = (await sessionManager.ListKeysAsync())
            .Single(static key => key.StartsWith("cron:agent-pipeline:", StringComparison.Ordinal));
        var session = await sessionManager.GetOrCreateAsync(sessionKey, "default");
        session.History.Any(entry =>
                entry.Role == MessageRole.Assistant &&
                entry.Content.StartsWith("provider-response:", StringComparison.Ordinal))
            .Should()
            .BeTrue();
    }

    [Fact]
    public async Task MaintenanceCronJob_Trigger_InvokesMemoryConsolidator()
    {
        using var scope = new TestScope();
        var consolidator = new RecordingMemoryConsolidator();
        using var factory = CreateFactory(
            scope,
            CronMaintenanceJob("consolidate-memory", "default"),
            services =>
            {
                services.RemoveAll<IMemoryConsolidator>();
                services.AddSingleton<IMemoryConsolidator>(consolidator);
            });

        using var client = factory.CreateClient();
        var triggerResponse = await client.PostAsync("/api/cron/maintenance:consolidate-memory/trigger", content: null);
        triggerResponse.EnsureSuccessStatusCode();

        await WaitForAsync(() => consolidator.Calls.Count > 0);
        consolidator.Calls.Should().Contain("default");
    }

    [Fact]
    public async Task SystemCronJob_Trigger_ExecutesRegisteredSystemAction()
    {
        using var scope = new TestScope();
        var action = new RecordingSystemAction("integration-action", "system-action-ok");
        using var factory = CreateFactory(
            scope,
            CronSystemJob("integration-action"),
            services => services.AddSingleton<ISystemAction>(action));

        using var client = factory.CreateClient();
        var triggerResponse = await client.PostAsync("/api/cron/system:integration-action/trigger", content: null);
        triggerResponse.EnsureSuccessStatusCode();

        await WaitForAsync(() => action.ExecutionCount > 0);
        action.ExecutionCount.Should().Be(1);
    }

    [Fact]
    public async Task CronApiEndpoints_ReturnExpectedData()
    {
        using var scope = new TestScope();
        using var factory = CreateFactory(
            scope,
            CronAgentJob("api-job", "api prompt"));
        using var client = factory.CreateClient();

        (await client.PostAsync("/api/cron/api-job/trigger", content: null)).EnsureSuccessStatusCode();

        using var diScope = factory.Services.CreateScope();
        var cronService = diScope.ServiceProvider.GetRequiredService<ICronService>();
        await WaitForAsync(() => cronService.GetHistory("api-job").Count > 0);

        var allJobsPayload = JsonDocument.Parse(await client.GetStringAsync("/api/cron"));
        allJobsPayload.RootElement.EnumerateArray()
            .Any(static e => e.GetProperty("name").GetString() == "api-job")
            .Should()
            .BeTrue();

        var singlePayload = JsonDocument.Parse(await client.GetStringAsync("/api/cron/api-job"));
        singlePayload.RootElement.GetProperty("name").GetString().Should().Be("api-job");
        singlePayload.RootElement.GetProperty("history").EnumerateArray().Should().NotBeEmpty();

        var historyPayload = JsonDocument.Parse(await client.GetStringAsync("/api/cron/history"));
        historyPayload.RootElement.EnumerateArray()
            .Any(static e => e.GetProperty("jobName").GetString() == "api-job")
            .Should()
            .BeTrue();
    }

    [Fact]
    public async Task CronApiManualTrigger_ExecutesJobImmediately()
    {
        using var scope = new TestScope();
        using var factory = CreateFactory(
            scope,
            CronAgentJob("manual-job", "manual prompt"));
        using var client = factory.CreateClient();

        var triggerResponse = await client.PostAsync("/api/cron/manual-job/trigger", content: null);
        triggerResponse.EnsureSuccessStatusCode();

        var triggerBody = JsonDocument.Parse(await triggerResponse.Content.ReadAsStringAsync()).RootElement;
        triggerBody.GetProperty("triggered").GetBoolean().Should().BeTrue();
        triggerBody.GetProperty("jobName").GetString().Should().Be("manual-job");

        using var diScope = factory.Services.CreateScope();
        var cronService = diScope.ServiceProvider.GetRequiredService<ICronService>();
        await WaitForAsync(() => cronService.GetHistory("manual-job").Count > 0);
    }

    [Fact]
    public async Task CronApiEnableEndpoint_TogglesJobEnabledState()
    {
        using var scope = new TestScope();
        using var factory = CreateFactory(
            scope,
            CronAgentJob("toggle-job", "toggle prompt"));
        using var client = factory.CreateClient();

        var disableResponse = await client.PutAsJsonAsync("/api/cron/toggle-job/enable", new { enabled = false });
        disableResponse.EnsureSuccessStatusCode();
        var disabledPayload = JsonDocument.Parse(await disableResponse.Content.ReadAsStringAsync()).RootElement;
        disabledPayload.GetProperty("enabled").GetBoolean().Should().BeFalse();

        var disabledState = JsonDocument.Parse(await client.GetStringAsync("/api/cron/toggle-job")).RootElement;
        disabledState.GetProperty("enabled").GetBoolean().Should().BeFalse();

        var enableResponse = await client.PutAsJsonAsync("/api/cron/toggle-job/enable", new { enabled = true });
        enableResponse.EnsureSuccessStatusCode();

        var enabledState = JsonDocument.Parse(await client.GetStringAsync("/api/cron/toggle-job")).RootElement;
        enabledState.GetProperty("enabled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Startup_MigratesLegacyAgentCronJobs()
    {
        using var scope = new TestScope();
        var config = new Dictionary<string, string?>
        {
            ["BotNexus:Agents:Named:legacy-agent:CronJobs:0:Type"] = "agent",
            ["BotNexus:Agents:Named:legacy-agent:CronJobs:0:Schedule"] = "0 0 * * *",
            ["BotNexus:Agents:Named:legacy-agent:CronJobs:0:Prompt"] = "legacy prompt"
        };

        using var factory = CreateFactory(scope, configOverrides: config);
        using var client = factory.CreateClient();

        var payload = JsonDocument.Parse(await client.GetStringAsync("/api/cron")).RootElement;
        payload.EnumerateArray()
            .Any(static e => e.GetProperty("name").GetString() == "legacy-agent")
            .Should()
            .BeTrue();
    }

    [Fact]
    public async Task HealthEndpoint_ReportsCronServiceHealthyWhenRunning()
    {
        using var scope = new TestScope();
        using var factory = CreateFactory(
            scope,
            CronAgentJob("health-job", "health prompt"));
        using var client = factory.CreateClient();

        var healthPayload = JsonDocument.Parse(await client.GetStringAsync("/health")).RootElement;
        var cronStatus = healthPayload.GetProperty("checks").GetProperty("cron_service").GetProperty("status").GetString();
        cronStatus.Should().Be("Healthy");
    }

    [Fact]
    public async Task TriggeredCronJob_PublishesActivityStreamEvents()
    {
        using var scope = new TestScope();
        using var factory = CreateFactory(
            scope,
            CronAgentJob("activity-job", "activity prompt"));
        using var client = factory.CreateClient();

        using var diScope = factory.Services.CreateScope();
        var stream = diScope.ServiceProvider.GetRequiredService<IActivityStream>();
        using var subscription = stream.Subscribe();

        (await client.PostAsync("/api/cron/activity-job/trigger", content: null)).EnsureSuccessStatusCode();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var events = new List<ActivityEvent>();
        while (events.Count < 2)
        {
            var next = await subscription.ReadAsync(cts.Token);
            if (next.Channel != "cron")
                continue;
            events.Add(next);
        }

        events.Any(e => e.Metadata is not null && Equals(e.Metadata["event"], "cron.job.started")).Should().BeTrue();
        events.Any(e => e.Metadata is not null && Equals(e.Metadata["event"], "cron.job.completed")).Should().BeTrue();
    }

    private static WebApplicationFactory<Program> CreateFactory(
        TestScope scope,
        Dictionary<string, string?>? cronOverrides = null,
        Action<IServiceCollection>? configureServices = null,
        Dictionary<string, string?>? configOverrides = null)
    {
        var config = BaseConfig(scope);

        if (cronOverrides is not null)
        {
            foreach (var (key, value) in cronOverrides)
                config[key] = value;
        }

        if (configOverrides is not null)
        {
            foreach (var (key, value) in configOverrides)
                config[key] = value;
        }

        var provider = new ScriptedLlmProvider();

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, cb) => cb.AddInMemoryCollection(config));
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<ILlmProvider>();
                    services.AddSingleton<ILlmProvider>(provider);
                    configureServices?.Invoke(services);
                });
            });
    }

    private static Dictionary<string, string?> BaseConfig(TestScope scope)
    {
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["BotNexus:Gateway:ApiKey"] = string.Empty,
            ["BotNexus:Gateway:WebSocketEnabled"] = "true",
            ["BotNexus:Agents:Workspace"] = scope.WorkspacePath,
            ["BotNexus:Agents:Model"] = "test-model",
            ["BotNexus:Cron:Enabled"] = "true",
            ["BotNexus:Cron:TickIntervalSeconds"] = "1"
        };
    }

    private static Dictionary<string, string?> CronAgentJob(string name, string prompt)
    {
        return new Dictionary<string, string?>
        {
            [$"BotNexus:Cron:Jobs:{name}:Name"] = name,
            [$"BotNexus:Cron:Jobs:{name}:Type"] = "agent",
            [$"BotNexus:Cron:Jobs:{name}:Schedule"] = "0 0 * * *",
            [$"BotNexus:Cron:Jobs:{name}:Agent"] = "default",
            [$"BotNexus:Cron:Jobs:{name}:Prompt"] = prompt
        };
    }

    private static Dictionary<string, string?> CronMaintenanceJob(string action, params string[] agents)
    {
        var config = new Dictionary<string, string?>
        {
            [$"BotNexus:Cron:Jobs:{action}:Type"] = "maintenance",
            [$"BotNexus:Cron:Jobs:{action}:Schedule"] = "0 0 * * *",
            [$"BotNexus:Cron:Jobs:{action}:Action"] = action
        };

        for (var i = 0; i < agents.Length; i++)
            config[$"BotNexus:Cron:Jobs:{action}:Agents:{i}"] = agents[i];

        return config;
    }

    private static Dictionary<string, string?> CronSystemJob(string action)
    {
        return new Dictionary<string, string?>
        {
            [$"BotNexus:Cron:Jobs:{action}:Type"] = "system",
            [$"BotNexus:Cron:Jobs:{action}:Schedule"] = "0 0 * * *",
            [$"BotNexus:Cron:Jobs:{action}:Action"] = action
        };
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 10000)
    {
        var started = DateTimeOffset.UtcNow;
        while (!condition())
        {
            if ((DateTimeOffset.UtcNow - started).TotalMilliseconds > timeoutMs)
                throw new TimeoutException("Condition not met within timeout.");
            await Task.Delay(50);
        }
    }

    private sealed class TestScope : IDisposable
    {
        private readonly string? _previousHome;

        public TestScope()
        {
            RootPath = Path.Combine(AppContext.BaseDirectory, "test-artifacts", "cron-integration", Guid.NewGuid().ToString("N"));
            WorkspacePath = Path.Combine(RootPath, "workspace");
            HomePath = Path.Combine(RootPath, "home");
            Directory.CreateDirectory(WorkspacePath);
            Directory.CreateDirectory(HomePath);

            _previousHome = Environment.GetEnvironmentVariable("BOTNEXUS_HOME");
            Environment.SetEnvironmentVariable("BOTNEXUS_HOME", HomePath);
        }

        public string RootPath { get; }
        public string WorkspacePath { get; }
        public string HomePath { get; }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("BOTNEXUS_HOME", _previousHome);
            if (Directory.Exists(RootPath))
            {
                try
                {
                    Directory.Delete(RootPath, recursive: true);
                }
                catch
                {
                    // Best-effort cleanup.
                }
            }
        }
    }

    private sealed class ScriptedLlmProvider : ILlmProvider
    {
        public string DefaultModel => "test-model";
        public GenerationSettings Generation { get; set; } = new();

        public Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<string>>(new[] { DefaultModel });
        }

        public Task<LlmResponse> ChatAsync(ChatRequest request, CancellationToken cancellationToken = default)
        {
            var lastUser = request.Messages.LastOrDefault(static m => m.Role == "user")?.Content ?? string.Empty;
            return Task.FromResult(new LlmResponse($"provider-response:{lastUser}", FinishReason.Stop));
        }

        public async IAsyncEnumerable<StreamingChatChunk> ChatStreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await ChatAsync(request, cancellationToken);
            yield return StreamingChatChunk.FromContentDelta(response.Content);
        }
    }

    private sealed class RecordingMemoryConsolidator : IMemoryConsolidator
    {
        public ConcurrentBag<string> Calls { get; } = [];

        public Task<MemoryConsolidationResult> ConsolidateAsync(
            string agentName,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(agentName);
            return Task.FromResult(new MemoryConsolidationResult(true, 1, 1));
        }
    }

    private sealed class RecordingSystemAction(string name, string output) : ISystemAction
    {
        public int ExecutionCount;

        public string Name => name;
        public string Description => "integration test action";

        public Task<string> ExecuteAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref ExecutionCount);
            return Task.FromResult(output);
        }
    }
}
