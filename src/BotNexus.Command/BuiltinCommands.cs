using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;

namespace BotNexus.Command;

/// <summary>Registers built-in commands (/help, /reset, /status).</summary>
public static class BuiltinCommands
{
    /// <summary>Registers all built-in commands with the router.</summary>
    public static void Register(
        ICommandRouter router,
        ISessionManager sessionManager,
        IHeartbeatService? heartbeatService = null)
    {
        router.Register("/help", async (msg, ct) =>
        {
            await Task.CompletedTask;
            return """
                BotNexus Commands:
                /help    - Show this help message
                /reset   - Reset the current conversation session
                /status  - Show system status
                """;
        }, priority: 100);

        router.Register("/reset", async (msg, ct) =>
        {
            await sessionManager.ResetAsync(msg.SessionKey, ct).ConfigureAwait(false);
            return "✅ Session reset. Starting fresh!";
        }, priority: 100);

        router.Register("/status", async (msg, ct) =>
        {
            await Task.CompletedTask;
            var heartbeat = heartbeatService is not null
                ? $"Last heartbeat: {heartbeatService.LastBeat?.ToString("u") ?? "never"}"
                : "Heartbeat: disabled";
            return $"✅ BotNexus is running\n{heartbeat}";
        }, priority: 100);
    }
}
