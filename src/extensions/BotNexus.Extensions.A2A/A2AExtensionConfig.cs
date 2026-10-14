namespace BotNexus.Extensions.A2A;

/// <summary>Per-agent grant for provider-neutral A2A service profiles.</summary>
public sealed class A2AExtensionConfig
{
    /// <summary>The manifest and agent-configuration key for this extension.</summary>
    public const string ExtensionId = "botnexus-a2a";

    /// <summary>Gets or sets the profile IDs this agent may invoke.</summary>
    public IReadOnlyList<string> Profiles { get; init; } = [];
}
