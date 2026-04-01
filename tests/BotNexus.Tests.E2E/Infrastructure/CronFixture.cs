using System.Collections.Concurrent;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using BotNexus.Core.Models;
using BotNexus.Providers.Base;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Tests.E2E.Infrastructure;

/// <summary>
/// Bootstraps the full BotNexus Gateway with cron enabled, mock channels,
/// and deterministic providers for cron system E2E testing.
/// Unlike <see cref="MultiAgentFixture"/>, this fixture keeps CronService
/// running so cron jobs register at startup and can be triggered via API.
/// </summary>
public sealed class CronFixture : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;
    private readonly string _workspacePath = Path.Combine(
        AppContext.BaseDirectory, "cron-e2e-workspace", Guid.NewGuid().ToString("N"));

    public MockLlmProvider MockProvider { get; } = new();
    public MockWebChannel WebChannel { get; } = new();
    public MockApiChannel ApiChannel { get; } = new();
    public MockMemoryConsolidator MemoryConsolidator { get; } = new();

    public HttpClient Client => _client ??= _factory!.CreateClient();
    public IServiceProvider Services => _factory!.Services;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_workspacePath);
        Directory.CreateDirectory(Path.Combine(_workspacePath, "sessions"));

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddJsonFile(
                        Path.Combine(AppContext.BaseDirectory, "appsettings.Testing.json"),
                        optional: false);

                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["BotNexus:Agents:Workspace"] = _workspacePath,
                        ["BotNexus:ExtensionsPath"] = Path.Combine(_workspacePath, "extensions"),
                        ["BotNexus:Gateway:ApiKey"] = string.Empty,

                        // Enable cron with a 1-second tick for fast test execution
                        ["BotNexus:Cron:Enabled"] = "true",
                        ["BotNexus:Cron:TickIntervalSeconds"] = "1",

                        // --- Agent cron job (far-future schedule; manual trigger only) ---
                        ["BotNexus:Cron:Jobs:nova-briefing:Name"] = "nova-briefing",
                        ["BotNexus:Cron:Jobs:nova-briefing:Type"] = "agent",
                        ["BotNexus:Cron:Jobs:nova-briefing:Schedule"] = "0 0 1 1 *",
                        ["BotNexus:Cron:Jobs:nova-briefing:Agent"] = "nova",
                        ["BotNexus:Cron:Jobs:nova-briefing:Prompt"] = "What are the best pizzas?",
                        ["BotNexus:Cron:Jobs:nova-briefing:Enabled"] = "true",
                        ["BotNexus:Cron:Jobs:nova-briefing:Session"] = "persistent",
                        ["BotNexus:Cron:Jobs:nova-briefing:OutputChannels:0"] = "mock-web",

                        // --- System cron job (health-audit action) ---
                        ["BotNexus:Cron:Jobs:health-check:Type"] = "system",
                        ["BotNexus:Cron:Jobs:health-check:Schedule"] = "0 0 1 1 *",
                        ["BotNexus:Cron:Jobs:health-check:Action"] = "health-audit",
                        ["BotNexus:Cron:Jobs:health-check:Enabled"] = "true",

                        // --- Maintenance cron job (memory consolidation) ---
                        ["BotNexus:Cron:Jobs:memory-cleanup:Type"] = "maintenance",
                        ["BotNexus:Cron:Jobs:memory-cleanup:Schedule"] = "0 0 1 1 *",
                        ["BotNexus:Cron:Jobs:memory-cleanup:Action"] = "consolidate-memory",
                        ["BotNexus:Cron:Jobs:memory-cleanup:Enabled"] = "true",
                        ["BotNexus:Cron:Jobs:memory-cleanup:Agents:0"] = "nova",

                        // --- Toggle-test job (used for enable/disable scenario) ---
                        ["BotNexus:Cron:Jobs:toggle-test:Type"] = "system",
                        ["BotNexus:Cron:Jobs:toggle-test:Schedule"] = "0 0 1 1 *",
                        ["BotNexus:Cron:Jobs:toggle-test:Action"] = "check-updates",
                        ["BotNexus:Cron:Jobs:toggle-test:Enabled"] = "true",

                        // --- Legacy agent cron job (migration test) ---
                        ["BotNexus:Agents:Named:echo:CronJobs:0:Schedule"] = "0 0 1 1 *",
                        ["BotNexus:Agents:Named:echo:CronJobs:0:Type"] = "agent",
                        ["BotNexus:Agents:Named:echo:CronJobs:0:Prompt"] = "Echo from legacy cron",
                    });
                });

                builder.ConfigureServices(services =>
                {
                    // Replace LLM provider with deterministic mock
                    services.AddSingleton<ILlmProvider>(MockProvider);
                    services.AddSingleton(sp =>
                    {
                        var registry = new ProviderRegistry();
                        registry.Register("mock", MockProvider);
                        return registry;
                    });

                    // Replace memory consolidator with mock
                    services.AddSingleton<IMemoryConsolidator>(MemoryConsolidator);

                    // Register mock channels
                    services.AddSingleton<IChannel>(WebChannel);
                    services.AddSingleton<IChannel>(ApiChannel);
                });
            });

        // Force host to start so CronService and job registration begin
        _ = _factory.Server;

        // Ensure mock channels report as running (AgentCronJob checks IsRunning)
        await WebChannel.StartAsync();
        await ApiChannel.StartAsync();
    }

    public Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();
        try
        {
            if (Directory.Exists(_workspacePath))
                Directory.Delete(_workspacePath, recursive: true);
        }
        catch { /* best-effort cleanup */ }
        return Task.CompletedTask;
    }
}

/// <summary>
/// Deterministic mock for <see cref="IMemoryConsolidator"/> that records
/// calls and returns success without performing real LLM summarization.
/// </summary>
public sealed class MockMemoryConsolidator : IMemoryConsolidator
{
    private readonly ConcurrentBag<string> _consolidatedAgents = new();

    public IReadOnlyList<string> ConsolidatedAgents => [.. _consolidatedAgents];

    public Task<MemoryConsolidationResult> ConsolidateAsync(
        string agentName, CancellationToken cancellationToken = default)
    {
        _consolidatedAgents.Add(agentName);
        return Task.FromResult(new MemoryConsolidationResult(
            Success: true,
            DailyFilesProcessed: 3,
            EntriesConsolidated: 12));
    }
}
