using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.Primitives;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Channels.Test.Tests;

/// <summary>
/// Exercises the HTTP surface and <see cref="TestChannelClient"/> together against a real
/// ASP.NET Core host with the channel composed in-process.
/// </summary>
/// <remarks>
/// <para>
/// The client and the endpoints are one contract — route shape, request body, response shape — so
/// they are tested through each other. Asserting the endpoints with a hand-rolled request while the
/// client is tested against a stub would let the two drift and both suites stay green.
/// </para>
/// <para>
/// A real Kestrel host on an ephemeral port is used rather than a mocked pipeline because the thing
/// under test IS the HTTP surface: route templates, query binding, JSON body binding and status
/// codes are exactly what would be stubbed away.
/// </para>
/// </remarks>
public sealed class TestChannelHttpSurfaceTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private RecordingDispatcher _dispatcher = null!;
    private TestChannelAdapter _adapter = null!;
    private string _baseUrl = null!;

    private sealed class RecordingDispatcher : IChannelDispatcher
    {
        private readonly List<InboundMessage> _dispatched = [];
        private readonly Lock _sync = new();

        public IReadOnlyList<InboundMessage> Dispatched
        {
            get { lock (_sync) return [.. _dispatched]; }
        }

        public Task DispatchAsync(InboundMessage message, CancellationToken cancellationToken = default)
        {
            lock (_sync) _dispatched.Add(message);
            return Task.CompletedTask;
        }
    }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        // Ephemeral loopback port: several of these fixtures may run in one assembly and a fixed
        // port would make them collide with each other and with anything already on the box.
        builder.Configuration["urls"] = "http://127.0.0.1:0";
        builder.Logging.ClearProviders();
        builder.Services.AddBotNexusTestChannel(options => options.ChannelId = "telegram");

        _app = builder.Build();

        foreach (var contributor in _app.Services.GetServices<IEndpointContributor>())
            contributor.MapEndpoints(_app);

        await _app.StartAsync();

        _adapter = _app.Services.GetServices<IChannelAdapter>().OfType<TestChannelAdapter>().Single();
        _dispatcher = new RecordingDispatcher();
        await _adapter.StartAsync(_dispatcher);

        _baseUrl = _app.Urls.First();
    }

    public async Task DisposeAsync()
    {
        await _adapter.StopAsync();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private TestChannelClient CreateClient(string channelId = "telegram") => new(_baseUrl, channelId);

    [Fact]
    public async Task InjectMessageAsync_ReachesTheGatewayDispatcherThroughHttp()
    {
        using var client = CreateClient();

        await client.InjectMessageAsync("hello from portal", address: "chat-100", senderId: "user-9");

        var message = _dispatcher.Dispatched.ShouldHaveSingleItem();
        message.ChannelType.Value.ShouldBe("telegram");
        message.ChannelAddress.Value.ShouldBe("chat-100");
        message.Content.ShouldBe("hello from portal");
        message.SenderId.ShouldBe("user-9");
    }

    [Fact]
    public async Task InjectMessageAsync_ThrowsNamingTheCause_WhenTheChannelIsNotLoaded()
    {
        // The route segment is matched against the ADAPTER's own channel key, so an unregistered
        // key must 404 rather than being served by whichever test adapter happens to exist.
        using var client = CreateClient(channelId: "slack");

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => client.InjectMessageAsync("hi", "chat-1"));

        ex.Message.ShouldContain("not loaded");
    }

    [Fact]
    public async Task GetOutboundAsync_ReturnsDeliveriesFilteredByAddress()
    {
        using var client = CreateClient();
        await _adapter.SendAsync(Message("chat-100", "for one hundred"));
        await _adapter.SendAsync(Message("chat-200", "for two hundred"));

        var filtered = await client.GetOutboundAsync("chat-100");
        var all = await client.GetOutboundAsync();

        filtered.ShouldHaveSingleItem().Content.ShouldBe("for one hundred");
        all.Count.ShouldBe(2);
    }

    [Fact]
    public async Task WaitForMessageAsync_ReturnsTheCompleteMessageAndIgnoresStreamDeltas()
    {
        using var client = CreateClient();

        await _adapter.SendStreamDeltaAsync(
            new ChannelStreamTarget(
                ConversationId.From("c_1"),
                SessionId.From("s-1"),
                ChannelAddress.From("chat-100")),
            "User Sai");
        await _adapter.SendAsync(Message("chat-100", "User Said: hello"));

        var record = await client.WaitForMessageAsync("chat-100", timeout: TimeSpan.FromSeconds(2));

        // The delta is a strict prefix of the complete message, so a client that failed to skip
        // deltas would return "User Sai" and the assertion below would read as a content defect.
        record.Content.ShouldBe("User Said: hello");
        record.IsStreamDelta.ShouldBeFalse();
    }

    [Fact]
    public async Task WaitForMessageAsync_TimesOutReportingWhatWasActuallyCaptured()
    {
        using var client = CreateClient();
        await _adapter.SendAsync(Message("chat-100", "an unrelated delivery"));

        var ex = await Should.ThrowAsync<TimeoutException>(() => client.WaitForMessageAsync(
            "chat-100",
            record => record.Content.Contains("never happens", StringComparison.Ordinal),
            timeout: TimeSpan.FromMilliseconds(300),
            pollInterval: TimeSpan.FromMilliseconds(50)));

        // A bare "timed out" message sends the reader hunting for a delivery bug when the real
        // problem is usually that the message arrived with different content.
        ex.Message.ShouldContain("an unrelated delivery");
    }

    [Fact]
    public async Task ClearOutboundAsync_ClearsOnlyTheNamedAddress()
    {
        using var client = CreateClient();
        await _adapter.SendAsync(Message("chat-100", "a"));
        await _adapter.SendAsync(Message("chat-200", "b"));

        await client.ClearOutboundAsync("chat-100");

        (await client.GetOutboundAsync("chat-100")).ShouldBeEmpty();
        (await client.GetOutboundAsync("chat-200")).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task GetLogsAsync_ExposesStructuredGatewayLogEntries()
    {
        using var client = CreateClient();
        await client.ClearLogsAsync();

        var logger = _app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("BotNexus.Gateway.Channels");
        logger.LogInformation("fan-out delivered to {ChannelType}:{Address}", "telegram", "chat-100");

        var snapshot = await client.GetLogsAsync();

        snapshot.IsComplete.ShouldBeTrue();

        // The capture is host-wide by design - it also records ASP.NET's own request/routing
        // entries, including those produced by the very call that reads it. Asserting on an
        // exclusive count would therefore be asserting a falsehood about the buffer. Selecting the
        // specific entry is the stronger claim anyway: it survives framework log-level changes.
        var entry = snapshot.Entries
            .Where(candidate => candidate.Category == "BotNexus.Gateway.Channels")
            .ShouldHaveSingleItem();

        entry.Message.ShouldBe("fan-out delivered to telegram:chat-100");
        entry.Properties["ChannelType"].ShouldBe("telegram");
        entry.Properties["Address"].ShouldBe("chat-100");
    }

    [Fact]
    public async Task ClearLogsAsync_DiscardsEntriesCapturedBeforeTheClear()
    {
        using var client = CreateClient();
        var marker = $"marker-{Guid.NewGuid():N}";
        var logger = _app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("cat");
        logger.LogInformation("{Marker}", marker);

        (await client.GetLogsAsync()).Entries
            .ShouldContain(entry => entry.Message.Contains(marker, StringComparison.Ordinal));

        await client.ClearLogsAsync();

        // A unique marker, not an empty buffer: the read itself logs ASP.NET request entries, so
        // "empty" can never be observed through the HTTP surface and asserting it would be a claim
        // about the harness rather than about Clear.
        (await client.GetLogsAsync()).Entries
            .ShouldNotContain(entry => entry.Message.Contains(marker, StringComparison.Ordinal));
    }

    private static OutboundMessage Message(string address, string content) => new()
    {
        ChannelType = ChannelKey.From("telegram"),
        ChannelAddress = ChannelAddress.From(address),
        Content = content,
    };
}
