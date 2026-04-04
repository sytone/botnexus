using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using Microsoft.Extensions.Configuration;

namespace BotNexus.Tests.Extensions.Convention;

public sealed class ConventionEchoTool(IConfiguration configuration) : ITool
{
    public ToolDefinition Definition { get; } = new(
        "convention_echo",
        "Echoes configured values for testing",
        new Dictionary<string, ToolParameterSchema>());

    public Task<string> ExecuteAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
    {
        var message = configuration["Message"] ?? "unset";
        return Task.FromResult($"convention:{message}");
    }
}

public sealed class ConventionAlphaChannel(IConfiguration configuration) : IChannel
{
    public string Name { get; } = $"alpha:{configuration["ChannelName"] ?? "unset"}";
    public string DisplayName => "Convention Alpha";
    public bool IsRunning => false;
    public bool SupportsStreaming => false;

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SendDeltaAsync(string chatId, string delta, IReadOnlyDictionary<string, object>? metadata = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public bool IsAllowed(string senderId) => true;
}

public sealed class ConventionBetaChannel(IConfiguration configuration, IMemoryStore memoryStore) : IChannel
{
    public string Name { get; } = $"beta:{configuration["ChannelName"] ?? "unset"}:{memoryStore.GetType().Name}";
    public string DisplayName => "Convention Beta";
    public bool IsRunning => false;
    public bool SupportsStreaming => false;

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SendDeltaAsync(string chatId, string delta, IReadOnlyDictionary<string, object>? metadata = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public bool IsAllowed(string senderId) => true;
}
