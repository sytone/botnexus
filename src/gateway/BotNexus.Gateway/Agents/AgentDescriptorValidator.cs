using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Agents;

internal static class AgentDescriptorValidator
{
    public static IReadOnlyList<string> Validate(
        AgentDescriptor descriptor,
        IEnumerable<string>? availableIsolationStrategies = null)
    {
        List<string> errors = [];

        if (string.IsNullOrWhiteSpace(descriptor.DisplayName))
            errors.Add("DisplayName is required.");

        if (string.IsNullOrWhiteSpace(descriptor.ModelId))
            errors.Add("ModelId is required.");

        if (string.IsNullOrWhiteSpace(descriptor.ApiProvider))
            errors.Add("ApiProvider is required.");

        if (descriptor.MaxConcurrentSessions < 0)
            errors.Add("MaxConcurrentSessions must be >= 0.");

        if (string.IsNullOrWhiteSpace(descriptor.IsolationStrategy))
        {
            errors.Add("IsolationStrategy is required.");
        }
        else if (availableIsolationStrategies is not null)
        {
            var strategyNames = availableIsolationStrategies.ToArray();
            if (strategyNames.Length > 0 && !strategyNames.Contains(descriptor.IsolationStrategy, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add(
                    $"IsolationStrategy '{descriptor.IsolationStrategy}' is not registered. " +
                    $"Available: {string.Join(", ", strategyNames.Order(StringComparer.OrdinalIgnoreCase))}.");
            }
        }

        return errors;
    }

    /// <summary>
    /// Configuration-source variant of <see cref="Validate(AgentDescriptor, IEnumerable{string}?)"/>
    /// that additionally rejects any descriptor declaring <see cref="AgentKind.SubAgent"/>.
    /// Sub-agents are runtime-only — they are created exclusively by
    /// <c>DefaultSubAgentManager.SpawnAsync</c> on the spawn path and never persisted as
    /// configuration. A config file or REST payload that asserts <c>Kind = SubAgent</c> is
    /// either a misconfiguration (a named agent the operator wanted to register would be
    /// silently denied <c>spawn_subagent</c>) or a privilege-confusion attempt; either way
    /// the safer response is to reject at load time.
    /// </summary>
    public static IReadOnlyList<string> ValidateForConfig(
        AgentDescriptor descriptor,
        IEnumerable<string>? availableIsolationStrategies = null)
    {
        var errors = new List<string>(Validate(descriptor, availableIsolationStrategies));

        if (descriptor.Kind == AgentKind.SubAgent)
        {
            errors.Add(
                "Kind = SubAgent is reserved for runtime-spawned sub-agents and may not be " +
                "declared in configuration or REST payloads. Remove the 'kind' property or set " +
                "it to 'Named'.");
        }

        return errors;
    }
}
