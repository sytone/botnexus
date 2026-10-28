namespace BotNexus.Agent.Core.Loop;

/// <summary>
/// Carries a normally parsed provider failure into the existing retry lane without publishing a
/// terminal transcript message for each attempt. Only attempts with no exposed semantics qualify.
/// </summary>
internal sealed class ProviderInBandFailureException : Exception
{
    public ProviderFailureClass FailureClass { get; }
    public string DiagnosticDetail { get; }

    public ProviderInBandFailureException(string provider, ProviderFailureClass failureClass, string? detail)
        : base($"Provider '{provider}' failed ({failureClass}); automatic provider-attempt recovery did not complete. Retry or resume after checking provider status.")
    {
        FailureClass = failureClass;
        DiagnosticDetail = detail is { Length: > 512 } ? detail[..512] : detail ?? "No provider detail.";
    }
}
