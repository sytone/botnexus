namespace BotNexus.Gateway.Configuration;

/// <summary>Exposes the exact raw document from a configuration provider's last successful load.</summary>
internal interface IAcceptedRawConfigDocumentProvider
{
    /// <summary>Returns a deep clone so normalization cannot mutate provider-owned last-known-good state.</summary>
    ConfigDocument? GetAcceptedRawDocument();
}
