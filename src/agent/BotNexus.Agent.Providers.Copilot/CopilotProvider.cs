using System.Net.Http.Headers;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Utilities;

namespace BotNexus.Agent.Providers.Copilot;

/// <summary>
/// Copilot helper utilities used by standard Anthropic/OpenAI providers.
/// </summary>
public static class CopilotProvider
{
    public const string ProviderId = "github-copilot";

    public static string? ResolveApiKey(string? configuredApiKey = null)
    {
        return configuredApiKey
               ?? EnvironmentApiKeys.GetApiKey(ProviderId);
    }

    public static void ApplyDynamicHeaders(HttpRequestHeaders headers, IReadOnlyList<Message> messages)
    {
        var hasImages = CopilotHeaders.HasVisionInput(messages);
        foreach (var (key, value) in CopilotHeaders.BuildDynamicHeaders(messages, hasImages))
        {
            headers.TryAddWithoutValidation(key, value);
        }
    }
}
