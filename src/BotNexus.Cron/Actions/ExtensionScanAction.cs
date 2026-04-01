using System.Text;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Extensions;

namespace BotNexus.Cron.Actions;

public sealed class ExtensionScanAction(
    IEnumerable<ExtensionServiceRegistration>? registrations = null,
    ExtensionLoadContextStore? loadContextStore = null) : ISystemAction
{
    private readonly IReadOnlyList<ExtensionServiceRegistration> _registrations = registrations?.ToList() ?? [];
    private readonly ExtensionLoadContextStore? _loadContextStore = loadContextStore;

    public string Name => "extension-scan";
    public string Description => "Lists dynamically loaded extensions and their registration status.";

    public Task<string> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        if (_registrations.Count == 0)
            return Task.FromResult("[extension-scan] No extension services are currently registered.");

        var sb = new StringBuilder();
        sb.Append("[extension-scan] Registered extension services: ");
        sb.Append(_registrations.Count);

        var byType = _registrations
            .GroupBy(static registration => registration.ServiceType.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in byType)
        {
            var keys = group.Select(static registration => registration.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static key => key, StringComparer.OrdinalIgnoreCase);
            sb.AppendLine();
            sb.Append("- ");
            sb.Append(group.Key);
            sb.Append(": ");
            sb.Append(string.Join(", ", keys));
        }

        if (_loadContextStore is not null)
        {
            sb.AppendLine();
            sb.Append("Load contexts: ");
            sb.Append(_loadContextStore.Contexts.Count);
        }

        return Task.FromResult(sb.ToString());
    }
}
