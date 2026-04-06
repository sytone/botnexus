using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Agents;

internal static class AgentDescriptorValidator
{
    public static IReadOnlyList<string> Validate(
        AgentDescriptor descriptor,
        IEnumerable<string>? availableIsolationStrategies = null)
    {
        List<string> errors = [];

        if (string.IsNullOrWhiteSpace(descriptor.AgentId))
            errors.Add("AgentId is required.");

        if (string.IsNullOrWhiteSpace(descriptor.DisplayName))
            errors.Add("DisplayName is required.");

        if (string.IsNullOrWhiteSpace(descriptor.ModelId))
            errors.Add("ModelId is required.");

        if (string.IsNullOrWhiteSpace(descriptor.ApiProvider))
            errors.Add("ApiProvider is required.");

        if (!string.IsNullOrWhiteSpace(descriptor.SystemPrompt) &&
            !string.IsNullOrWhiteSpace(descriptor.SystemPromptFile))
        {
            errors.Add("SystemPrompt and SystemPromptFile are mutually exclusive.");
        }

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
}
