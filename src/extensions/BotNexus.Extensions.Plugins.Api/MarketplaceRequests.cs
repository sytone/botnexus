namespace BotNexus.Extensions.Plugins.Api;

/// <summary>Registers a source without fetching or executing its contents.</summary>
/// <param name="Id">Lowercase configuration-safe registration identity.</param>
/// <param name="Source">Credential-free absolute Git repository URL.</param>
/// <param name="Reference">Optional Git branch, tag, or commit.</param>
public sealed record MarketplaceAddRequest(string Id, string Source, string? Reference = null);

/// <summary>Replaces source metadata only; omitting the reference restores the repository default.</summary>
/// <param name="Source">Credential-free absolute Git repository URL.</param>
/// <param name="Reference">Optional Git branch, tag, or commit.</param>
public sealed record MarketplaceRegistrationRequest(string Source, string? Reference = null);

/// <summary>A structured error shared by marketplace registration operations.</summary>
/// <param name="Error">Actionable validation, missing identity, or conflict diagnostic.</param>
public sealed record MarketplaceError(string Error);
