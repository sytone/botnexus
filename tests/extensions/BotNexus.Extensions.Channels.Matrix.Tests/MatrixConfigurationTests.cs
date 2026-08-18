using BotNexus.Extensions.Channels.Matrix.Tests.Fakes;
using BotNexus.Gateway.Abstractions.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Extensions.Channels.Matrix.Tests;

/// <summary>
/// Tests for Matrix configuration binding, the dynamic-extension self-bind fallback, and the DI
/// registration surface.
/// </summary>
public sealed class MatrixConfigurationTests
{
    [Fact]
    public void Options_BindPerAgentAccountsFromTheChannelsMatrixSection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["channels:matrix:homeserver"] = "https://matrix.example.com",
                ["channels:matrix:syncTimeoutMs"] = "15000",
                ["channels:matrix:agents:farnsworth:userId"] = "@farnsworth:example.com",
                ["channels:matrix:agents:farnsworth:accessToken"] = "syt_a",
                ["channels:matrix:agents:farnsworth:autoJoin"] = "true",
                ["channels:matrix:agents:nova:userId"] = "@nova:example.com",
                ["channels:matrix:agents:nova:accessToken"] = "syt_b",
                ["channels:matrix:agents:nova:autoJoin"] = "false",
            })
            .Build();

        var options = new MatrixChannelOptions();
        configuration.GetSection("channels:matrix").Bind(options);

        options.Homeserver.ShouldBe("https://matrix.example.com");
        options.SyncTimeoutMs.ShouldBe(15000);
        options.Agents.Count.ShouldBe(2);
        options.Agents["farnsworth"].UserId.ShouldBe("@farnsworth:example.com");
        options.Agents["farnsworth"].AutoJoin.ShouldBeTrue();
        options.Agents["nova"].AccessToken.ShouldBe("syt_b");
        options.Agents["nova"].AutoJoin.ShouldBeFalse();
    }

    [Fact]
    public void Adapter_WithUnboundOptions_SelfBindsFromConfiguration()
    {
        // The dynamic-extension load path registers the adapter without ever calling
        // AddBotNexusMatrixChannel, so IOptions resolves empty and the adapter must bind itself.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["channels:matrix:homeserver"] = "https://matrix.example.com",
                ["channels:matrix:agents:farnsworth:userId"] = "@farnsworth:example.com",
                ["channels:matrix:agents:farnsworth:accessToken"] = "syt_a",
            })
            .Build();

        var factory = new FakeMatrixClientFactory();
        var adapter = new MatrixChannelAdapter(
            NullLogger<MatrixChannelAdapter>.Instance,
            new OptionsWrapper<MatrixChannelOptions>(new MatrixChannelOptions()),
            factory,
            configuration);

        adapter.GetAccountCount().ShouldBe(1);
        factory.Credentials["farnsworth"].UserId.ShouldBe("@farnsworth:example.com");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ResolveSyncTimeoutMs_NonPositiveFallsBackToTheDefault(int configured)
    {
        // Zero must not be read as "poll with no wait" — that would busy-spin against the
        // homeserver rather than long-poll.
        new MatrixChannelOptions { SyncTimeoutMs = configured }
            .ResolveSyncTimeoutMs()
            .ShouldBe(MatrixChannelOptions.DefaultSyncTimeoutMs);
    }

    [Fact]
    public void ResolveSyncTimeoutMs_PositiveIsHonoured() =>
        new MatrixChannelOptions { SyncTimeoutMs = 5000 }.ResolveSyncTimeoutMs().ShouldBe(5000);

    [Fact]
    public void ResolveStreamingBufferMs_ZeroIsAValidConfiguredValue()
    {
        // Unlike the sync timeout, zero here is meaningful: edit on every delta.
        new MatrixChannelOptions { StreamingBufferMs = 0 }.ResolveStreamingBufferMs().ShouldBe(0);
    }

    [Fact]
    public void ResolveStreamingBufferMs_NegativeFallsBackToTheDefault() =>
        new MatrixChannelOptions { StreamingBufferMs = -5 }
            .ResolveStreamingBufferMs()
            .ShouldBe(MatrixChannelOptions.DefaultStreamingBufferMs);

    [Fact]
    public void AddBotNexusMatrixChannel_RegistersTheAdapterAndDefaultFactory()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBotNexusMatrixChannel(o => o.Homeserver = "https://matrix.example.com");

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IMatrixClientFactory>().ShouldBeOfType<DefaultMatrixClientFactory>();
        provider.GetServices<IChannelAdapter>().OfType<MatrixChannelAdapter>().ShouldHaveSingleItem();
        provider.GetRequiredService<IOptions<MatrixChannelOptions>>().Value
            .Homeserver.ShouldBe("https://matrix.example.com");
    }

    [Fact]
    public void AddBotNexusMatrixChannel_KeepsACustomFactoryRegisteredBeforehand()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMatrixClientFactory>(new FakeMatrixClientFactory());
        services.AddBotNexusMatrixChannel();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IMatrixClientFactory>().ShouldBeOfType<FakeMatrixClientFactory>();
    }
}

/// <summary>
/// Tests for adapter start/stop lifecycle, including the resumable-start latch.
/// </summary>
public sealed class MatrixChannelAdapterLifecycleTests
{
    private static MatrixChannelOptions BuildOptions()
    {
        var options = new MatrixChannelOptions { Homeserver = "https://matrix.example.com" };
        options.Agents["farnsworth"] = new MatrixAccountConfig
        {
            UserId = "@farnsworth:example.com",
            AccessToken = "syt_a",
        };
        return options;
    }

    [Fact]
    public async Task Start_MarksTheAdapterRunningAndStop_ClearsIt()
    {
        var adapter = new MatrixChannelAdapter(
            NullLogger<MatrixChannelAdapter>.Instance,
            new OptionsWrapper<MatrixChannelOptions>(BuildOptions()),
            new FakeMatrixClientFactory());

        adapter.IsRunning.ShouldBeFalse();

        await adapter.StartAsync(new NoOpDispatcher());
        adapter.IsRunning.ShouldBeTrue();

        await adapter.StopAsync();
        adapter.IsRunning.ShouldBeFalse();
    }

    [Fact]
    public async Task Start_UsesTheConfiguredSyncTimeoutOnTheWire()
    {
        var factory = new FakeMatrixClientFactory();
        var options = BuildOptions();
        options.SyncTimeoutMs = 1234;

        var adapter = new MatrixChannelAdapter(
            NullLogger<MatrixChannelAdapter>.Instance,
            new OptionsWrapper<MatrixChannelOptions>(options),
            factory);

        await adapter.StartAsync(new NoOpDispatcher());

        var client = factory.ClientFor("farnsworth");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (client.SyncTimeouts.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        await adapter.StopAsync();

        client.SyncTimeouts.ShouldNotBeEmpty();
        client.SyncTimeouts[0].ShouldBe(1234);
    }

    [Fact]
    public async Task Start_FirstSyncSendsNoSinceToken()
    {
        var factory = new FakeMatrixClientFactory();
        var adapter = new MatrixChannelAdapter(
            NullLogger<MatrixChannelAdapter>.Instance,
            new OptionsWrapper<MatrixChannelOptions>(BuildOptions()),
            factory);

        await adapter.StartAsync(new NoOpDispatcher());

        var client = factory.ClientFor("farnsworth");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (client.SinceTokens.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        await adapter.StopAsync();

        client.SinceTokens.ShouldNotBeEmpty();
        client.SinceTokens[0].ShouldBeNull();
    }

    [Fact]
    public async Task Start_AdvancesTheSinceTokenAfterProcessingABatch()
    {
        // Continuity across restarts depends on the token advancing, and it must advance only AFTER
        // the batch is processed so a crash replays rather than skips.
        var factory = new FakeMatrixClientFactory();
        var client = factory.ClientFor("farnsworth");
        client.EnqueueSync(new MatrixSyncResponse { NextBatch = "s_next" });

        var adapter = new MatrixChannelAdapter(
            NullLogger<MatrixChannelAdapter>.Instance,
            new OptionsWrapper<MatrixChannelOptions>(BuildOptions()),
            factory);

        await adapter.StartAsync(new NoOpDispatcher());

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (client.SinceTokens.Count < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        await adapter.StopAsync();

        client.SinceTokens.Count.ShouldBeGreaterThanOrEqualTo(2);
        client.SinceTokens[1].ShouldBe("s_next");
    }

    [Fact]
    public async Task Start_TerminalAuthFailure_ParksTheLoopInsteadOfRetryingForever()
    {
        // A revoked token cannot clear by retrying; the loop must stop rather than produce an
        // unbounded stream of failed syncs.
        var factory = new FakeMatrixClientFactory();
        var client = factory.ClientFor("farnsworth");
        client.EnqueueSyncFailure(new MatrixApiException(
            System.Net.HttpStatusCode.Unauthorized, "M_UNKNOWN_TOKEN", "token revoked"));

        var adapter = new MatrixChannelAdapter(
            NullLogger<MatrixChannelAdapter>.Instance,
            new OptionsWrapper<MatrixChannelOptions>(BuildOptions()),
            factory);

        await adapter.StartAsync(new NoOpDispatcher());

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (client.SinceTokens.Count < 1 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        // Give the loop room to issue a second sync if it were going to retry.
        await Task.Delay(300);
        var observed = client.SinceTokens.Count;

        await adapter.StopAsync();

        observed.ShouldBe(1);
    }

    private sealed class NoOpDispatcher : IChannelDispatcher
    {
        public Task DispatchAsync(
            BotNexus.Gateway.Abstractions.Models.InboundMessage message,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
