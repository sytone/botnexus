using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Agents;

internal static class AgentDescriptorValidator
{
    public static IReadOnlyList<string> Validate(AgentDescriptor descriptor)
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

        return errors;
    }
}
