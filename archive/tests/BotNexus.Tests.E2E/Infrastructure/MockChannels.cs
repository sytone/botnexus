using System.Collections.Concurrent;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;

namespace BotNexus.Tests.E2E.Infrastructure;

/// <summary>
/// Base class for mock channels that capture sent messages in-memory
/// for test assertions. No external service dependencies.
/// </summary>
public abstract class MockChannelBase : IChannel
{
    private readonly ConcurrentQueue<OutboundMessage> _sentMessages = new();
    private readonly ConcurrentDictionary<string, List<string>> _deltasByChat = new();

    public abstract string Name { get; }
    public abstract string DisplayName { get; }
    public bool IsRunning { get; private set; }
    public bool SupportsStreaming => true;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        IsRunning = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        IsRunning = false;
        return Task.CompletedTask;
    }

    public Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        _sentMessages.Enqueue(message);
        return Task.CompletedTask;
    }

    public Task SendDeltaAsync(string chatId, string delta, IReadOnlyDictionary<string, object>? metadata = null, CancellationToken cancellationToken = default)
    {
        var deltas = _deltasByChat.GetOrAdd(chatId, _ => new List<string>());
        lock (deltas)
        {
            deltas.Add(delta);
        }
        return Task.CompletedTask;
    }

    public bool IsAllowed(string senderId) => true;

    /// <summary>Gets all outbound messages sent through this channel.</summary>
    public IReadOnlyList<OutboundMessage> GetSentMessages() => [.. _sentMessages];

    /// <summary>Gets outbound messages filtered by chat ID.</summary>
    public IReadOnlyList<OutboundMessage> GetMessagesForChat(string chatId)
        => _sentMessages.Where(m => m.ChatId == chatId).ToList();

    /// <summary>Waits for at least one outbound message to arrive, with timeout.</summary>
    public async Task<OutboundMessage> WaitForResponseAsync(string? chatId = null, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            var messages = chatId is null ? GetSentMessages() : GetMessagesForChat(chatId);
            if (messages.Count > 0)
                return messages[^1];
            await Task.Delay(50);
        }
        throw new TimeoutException($"No response received on channel '{Name}' within timeout" +
            (chatId is not null ? $" for chat '{chatId}'" : string.Empty));
    }

    /// <summary>Waits until at least <paramref name="count"/> messages have been sent.</summary>
    public async Task<IReadOnlyList<OutboundMessage>> WaitForMessagesAsync(int count, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            var messages = GetSentMessages();
            if (messages.Count >= count)
                return messages;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Expected {count} messages on channel '{Name}' but got {_sentMessages.Count}");
    }

    /// <summary>Clears all captured messages for a fresh test scenario.</summary>
    public void Reset()
    {
        while (_sentMessages.TryDequeue(out _)) { }
        _deltasByChat.Clear();
    }
}

/// <summary>Mock channel simulating a web-based chat interface.</summary>
public sealed class MockWebChannel : MockChannelBase
{
    public override string Name => "mock-web";
    public override string DisplayName => "Mock Web Channel";
}

/// <summary>Mock channel simulating an API-based interaction.</summary>
public sealed class MockApiChannel : MockChannelBase
{
    public override string Name => "mock-api";
    public override string DisplayName => "Mock API Channel";
}
