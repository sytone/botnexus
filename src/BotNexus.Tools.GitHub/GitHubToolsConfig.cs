namespace BotNexus.Tools.GitHub;

/// <summary>Configuration for the GitHub extension tools.</summary>
public class GitHubToolsConfig
{
    /// <summary>GitHub personal access token (PAT). Optional for public repositories.</summary>
    public string? Token { get; set; }

    /// <summary>Default owner/organisation used when not supplied in a tool call.</summary>
    public string? DefaultOwner { get; set; }

    /// <summary>GitHub API base URL. Defaults to <c>https://api.github.com</c>.
    /// Override for GitHub Enterprise Server.</summary>
    public string ApiBase { get; set; } = "https://api.github.com";
}
