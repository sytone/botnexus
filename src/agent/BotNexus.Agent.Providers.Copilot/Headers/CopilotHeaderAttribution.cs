namespace BotNexus.Agent.Providers.Copilot.Headers;

/// <summary>Explicit host resolution result when no verified account scope is available.</summary>
public enum CopilotHeaderAttribution
{
    /// <summary>Credential resolution could not establish a scope; do not assign a default account.</summary>
    Unavailable
}
