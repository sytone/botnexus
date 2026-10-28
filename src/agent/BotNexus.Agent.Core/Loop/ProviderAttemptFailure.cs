namespace BotNexus.Agent.Core.Loop;

/// <summary>Retains the original failure classification when presentation replaces provider prose.</summary>
internal sealed class ProviderAttemptFailure
{
    public ProviderFailureClass? Classification { get; set; }
    public string? DiagnosticDetail { get; set; }
}
