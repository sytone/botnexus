using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotNexus.Gateway.Abstractions.Extensions;

/// <summary>
/// Contributes one independently owned source to cross-domain search.
/// </summary>
public interface ISearchContributor
{
    /// <summary>Gets the stable machine-readable source identifier.</summary>
    string SourceId { get; }

    /// <summary>Gets the source label shown to users.</summary>
    string Label { get; }

    /// <summary>Gets whether the source is currently available for search.</summary>
    bool IsAvailable { get; }

    /// <summary>Gets whether this source can make an evidence-based provenance trust assessment.</summary>
    bool CanAssessProvenanceTrust { get; }

    /// <summary>Searches this source within the caller-supplied result bound.</summary>
    /// <param name="request">The query and maximum number of results to return.</param>
    /// <param name="cancellationToken">Cancels outstanding source work.</param>
    /// <returns>Results ordered by this source's local relevance policy.</returns>
    Task<IReadOnlyList<SearchResult>> SearchAsync(
        SearchRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Describes one bounded source-local search.
/// </summary>
/// <param name="Query">The source-local query.</param>
/// <param name="MaxResults">The maximum number of results the contributor may return.</param>
public sealed record SearchRequest(string Query, int MaxResults);

/// <summary>
/// Groups search results under their source identity without imposing cross-source ranking.
/// </summary>
/// <param name="SourceId">Stable machine-readable source identifier.</param>
/// <param name="Label">Source label shown to users.</param>
/// <param name="Results">Results in source-local order.</param>
public sealed record SearchSourceGroup(
    string SourceId,
    string Label,
    IReadOnlyList<SearchResult> Results);

/// <summary>
/// Represents one source-local search result.
/// </summary>
public sealed record SearchResult
{
    /// <summary>Creates a source-local search result.</summary>
    public SearchResult(
        string Title,
        string Snippet,
        string Target,
        DateTimeOffset Timestamp,
        double? Relevance = null,
        SearchProvenanceTrust ProvenanceTrust = SearchProvenanceTrust.Untrusted)
    {
        this.Title = Title;
        this.Snippet = Snippet;
        this.Target = Target;
        this.Timestamp = Timestamp;
        this.Relevance = Relevance;
        this.ProvenanceTrust = ProvenanceTrust;
    }

    /// <summary>Gets the result title.</summary>
    public string Title { get; init; }

    /// <summary>Gets the result summary.</summary>
    public string Snippet { get; init; }

    /// <summary>Gets the source-owned navigation target.</summary>
    public string Target { get; init; }

    /// <summary>Gets the source event or content timestamp.</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Gets optional source-local relevance. Values are not comparable across sources.</summary>
    public double? Relevance { get; init; }

    private SearchProvenanceTrust _provenanceTrust;

    /// <summary>
    /// Gets explicit provenance trust. Missing, null, unknown, or undefined values are untrusted.
    /// </summary>
    public SearchProvenanceTrust ProvenanceTrust
    {
        get => _provenanceTrust == SearchProvenanceTrust.Trusted
            ? SearchProvenanceTrust.Trusted
            : SearchProvenanceTrust.Untrusted;
        init => _provenanceTrust = value;
    }
}

/// <summary>
/// Records whether a source can attest the provenance of a result.
/// </summary>
[JsonConverter(typeof(SearchProvenanceTrustJsonConverter))]
public enum SearchProvenanceTrust
{
    /// <summary>The result is not explicitly attested by its source.</summary>
    Untrusted = 0,

    /// <summary>The source explicitly attests the result's provenance.</summary>
    Trusted = 1
}

internal sealed class SearchProvenanceTrustJsonConverter : JsonConverter<SearchProvenanceTrust>
{
    public override bool HandleNull => true;

    public override SearchProvenanceTrust Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            return SearchProvenanceTrust.Untrusted;
        }

        return string.Equals(reader.GetString(), "trusted", StringComparison.OrdinalIgnoreCase)
            ? SearchProvenanceTrust.Trusted
            : SearchProvenanceTrust.Untrusted;
    }

    public override void Write(
        Utf8JsonWriter writer,
        SearchProvenanceTrust value,
        JsonSerializerOptions options)
    {
        writer.WriteStringValue(value == SearchProvenanceTrust.Trusted ? "trusted" : "untrusted");
    }
}
