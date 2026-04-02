using BotNexus.Core.Configuration;

namespace BotNexus.Providers.Copilot;

public sealed class CopilotConfig : ProviderConfig
{
    public const string DefaultApiBaseUrl = "https://api.githubcopilot.com";
    public const string DefaultModelName = "gpt-4o";
    public const string DefaultOAuthClientId = "Iv1.b507a08c87ecfe98";
    public const string CopilotTokenExchangeUrl = "https://api.github.com/copilot_internal/v2/token";
    public const string EditorVersion = "vscode/1.99.0";
    public const string EditorPluginVersion = "copilot-chat/0.26.0";
    public const string UserAgentValue = "BotNexus/0.1";

    public CopilotConfig()
    {
        Auth = "oauth";
        ApiBase = DefaultApiBaseUrl;
        DefaultModel = DefaultModelName;
    }

    public string OAuthClientId { get; set; } = DefaultOAuthClientId;
}
