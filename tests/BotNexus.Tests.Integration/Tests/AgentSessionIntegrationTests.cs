using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using BotNexus.Core.Extensions;
using BotNexus.Core.Models;
using BotNexus.Session;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BotNexus.Tests.Integration.Tests;

public class AgentSessionIntegrationTests : IDisposable
{
    private readonly string _tempPath;
    private readonly IServiceProvider _serviceProvider;

    public AgentSessionIntegrationTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), $"botnexus-int-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempPath);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotNexus:Agents:Workspace"] = _tempPath,
                ["BotNexus:Agents:Model"] = "gpt-4o"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        services.AddBotNexusCore(configuration);
        services.AddBotNexusSession();

        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task SessionManager_CreateMultipleSessions_AreIndependent()
    {
        var sessionManager = _serviceProvider.GetRequiredService<ISessionManager>();

        var session1 = await sessionManager.GetOrCreateAsync("s1", "agent");
        var session2 = await sessionManager.GetOrCreateAsync("s2", "agent");

        session1.AddEntry(new SessionEntry(MessageRole.User, "msg1", DateTimeOffset.UtcNow));
        await sessionManager.SaveAsync(session1);

        session2.History.Should().BeEmpty();
    }

    [Fact]
    public async Task SessionManager_ConcurrentAccess_IsThreadSafe()
    {
        var sessionManager = _serviceProvider.GetRequiredService<ISessionManager>();

        var tasks = Enumerable.Range(0, 10).Select(async i =>
        {
            var session = await sessionManager.GetOrCreateAsync($"concurrent-{i}", "agent");
            session.AddEntry(new SessionEntry(MessageRole.User, $"message {i}", DateTimeOffset.UtcNow));
            await sessionManager.SaveAsync(session);
        });

        await Task.WhenAll(tasks);

        for (int i = 0; i < 10; i++)
        {
            var session = await sessionManager.GetOrCreateAsync($"concurrent-{i}", "agent");
            session.History.Should().HaveCount(1);
        }
    }

    [Fact]
    public async Task MessageBus_HighThroughput_DeliversAllMessages()
    {
        var bus = _serviceProvider.GetRequiredService<IMessageBus>();
        var count = 100;
        var received = new List<InboundMessage>();

        var producerTask = Task.Run(async () =>
        {
            for (int i = 0; i < count; i++)
            {
                await bus.PublishAsync(new InboundMessage(
                    "test", "user", "chat", $"msg{i}",
                    DateTimeOffset.UtcNow, [], new Dictionary<string, object>()));
            }
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var consumerTask = Task.Run(async () =>
        {
            while (received.Count < count && !cts.IsCancellationRequested)
            {
                try
                {
                    var msg = await bus.ReadAsync(cts.Token);
                    received.Add(msg);
                }
                catch (OperationCanceledException) { break; }
            }
        });

        await Task.WhenAll(producerTask, consumerTask);

        received.Should().HaveCount(count);
    }

    public void Dispose()
    {
        if (_serviceProvider is IDisposable d) d.Dispose();
        if (Directory.Exists(_tempPath))
            Directory.Delete(_tempPath, recursive: true);
    }
}
