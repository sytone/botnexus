using System.Net.Http;

namespace BotNexus.Conversation.Tests;

/// <summary>
/// Collection definition for tests requiring the live dev gateway.
/// </summary>
[CollectionDefinition("LiveGateway")]
public class LiveGatewayCollection : ICollectionFixture<LiveGatewayFixture> { }

/// <summary>
/// Fixture that connects to the live dev gateway at http://localhost:5006.
/// Sets IsAvailable=false if the gateway is not running — all tests skip cleanly.
/// </summary>
public class LiveGatewayFixture : IAsyncLifetime
{
    private const string BaseUrl = "http://localhost:5006";
    private TestLogger? _logger;
    private TestSignalRClient? _signalR;

    public bool IsAvailable { get; private set; }
    public HttpClient Http { get; private set; } = default!;
    public TestSignalRClient SignalR => _signalR!;
    public ConversationApiClient Conversations { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        Http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        Conversations = new ConversationApiClient(Http);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var response = await Http.GetAsync("/health", cts.Token);
            IsAvailable = response.IsSuccessStatusCode;
        }
        catch
        {
            IsAvailable = false;
        }

        if (!IsAvailable) return;

        var logDir = Path.Combine(Path.GetTempPath(), "botnexus-conversation-tests");
        _logger = new TestLogger(logDir);
        _signalR = new TestSignalRClient(BaseUrl, _logger);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _signalR.ConnectAsync(cts.Token);
        }
        catch
        {
            IsAvailable = false;
        }
    }

    public async Task DisposeAsync()
    {
        if (_signalR is not null)
            await _signalR.DisposeAsync();
        Http.Dispose();
        _logger?.Dispose();
    }
}
