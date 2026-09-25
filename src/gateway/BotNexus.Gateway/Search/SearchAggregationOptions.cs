namespace BotNexus.Gateway.Search;

/// <summary>
/// Configures bounded execution for independently owned search contributors.
/// </summary>
public sealed class SearchAggregationOptions
{
    /// <summary>Gets or sets the default deadline applied to each contributor independently.</summary>
    public TimeSpan DefaultSourceTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets source-specific deadline overrides keyed by contributor source identifier.</summary>
    public Dictionary<string, TimeSpan> SourceTimeouts { get; } = new(StringComparer.OrdinalIgnoreCase);
}
