using Microsoft.AspNetCore.SignalR.Client;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

namespace BotNexus.Probe.Gateway;

public sealed class GatewayHubClient(string gatewayBaseUrl)
{
    private readonly Channel<GatewayActivityDto> _activityChannel = Channel.CreateUnbounded<GatewayActivityDto>();
    private readonly ConcurrentQueue<GatewayActivityDto> _recent = new();
    private HubConnection? _connection;

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;
    public string? LastError { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_connection is not null)
        {
            return;
        }

        _connection = new HubConnectionBuilder()
            .WithUrl($"{gatewayBaseUrl.TrimEnd('/')}/hub/gateway")
            .WithAutomaticReconnect([
                TimeSpan.Zero,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10),
            ])
            .Build();

        _connection.Closed += async exception =>
        {
            LastError = exception?.Message;
            await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
        };

        _connection.Reconnected += async _ =>
        {
            LastError = null;
            await TrySubscribeAllAsync(CancellationToken.None);
        };

        _connection.On<GatewayActivityDto>("Activity", AddActivity);
        _connection.On<JsonElement>("Activity", activity => AddActivity(ConvertActivity(activity)));
        _connection.On<GatewayActivityDto>("GatewayActivity", AddActivity);

        try
        {
            await _connection.StartAsync(cancellationToken);
            await TrySubscribeAllAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_connection is null)
        {
            return;
        }

        await _connection.StopAsync(cancellationToken);
        await _connection.DisposeAsync();
        _connection = null;
    }

    public async IAsyncEnumerable<GatewayActivityDto> ReadActivityAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in _activityChannel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return item;
        }
    }

    public IReadOnlyList<GatewayActivityDto> GetRecent(int limit)
        => _recent.Reverse().Take(Math.Max(limit, 1)).ToList();

    private void AddActivity(GatewayActivityDto activity)
    {
        _activityChannel.Writer.TryWrite(activity);
        _recent.Enqueue(activity);
        while (_recent.Count > 1_000)
        {
            _recent.TryDequeue(out _);
        }
    }

    private async Task TrySubscribeAllAsync(CancellationToken cancellationToken)
    {
        if (_connection is null || _connection.State != HubConnectionState.Connected)
        {
            return;
        }

        try
        {
            await _connection.InvokeAsync("SubscribeAll", cancellationToken);
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
        }
    }

    private static GatewayActivityDto ConvertActivity(JsonElement element)
    {
        var eventId = TryGetString(element, "eventId") ?? Guid.NewGuid().ToString("N");
        var type = TryGetString(element, "type") ?? "unknown";
        var agentId = TryGetString(element, "agentId");
        var sessionId = TryGetString(element, "sessionId");
        var channelType = TryGetString(element, "channelType");
        var message = TryGetString(element, "message");
        var timestamp = DateTimeOffset.TryParse(TryGetString(element, "timestamp"), out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;

        Dictionary<string, object?>? data = null;
        if (element.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Object)
        {
            data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in dataElement.EnumerateObject())
            {
                data[property.Name] = property.Value.GetRawText();
            }
        }

        return new GatewayActivityDto(eventId, type, agentId, sessionId, channelType, message, timestamp, data);
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }
}
