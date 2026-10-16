namespace BotNexus.Agent.Core.ExtensionPoints.ToolResults;

/// <summary>Observable progress classification for one completed tool result.</summary>
public enum ToolProgressOutcome
{
    /// <summary>The result is unclassified; it neither increments nor resets repetition evidence.</summary>
    Neutral,

    /// <summary>The result establishes progress and resets run-local repetition evidence.</summary>
    Progress,

    /// <summary>The result supplies authoritative scope and evidence identities for repetition tracking.</summary>
    NoProgress
}
