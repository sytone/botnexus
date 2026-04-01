using System.Reflection;
using BotNexus.Core.Abstractions;

namespace BotNexus.Cron.Actions;

public sealed class CheckUpdatesAction : ISystemAction
{
    public string Name => "check-updates";
    public string Description => "Checks the currently running assembly version.";

    public Task<string> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var entry = Assembly.GetEntryAssembly();
        var version = entry?.GetName().Version?.ToString() ?? "unknown";
        var assemblyName = entry?.GetName().Name ?? "BotNexus";
        return Task.FromResult($"[{Name}] {assemblyName} is running version {version}. External update feed integration is pending.");
    }
}
