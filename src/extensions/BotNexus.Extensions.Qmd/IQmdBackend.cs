namespace BotNexus.Extensions.Qmd;

/// <summary>
/// Abstraction over the QMD document search backend.
/// The production implementation (<c>QmdCliBackend</c> in a follow-up issue)
/// wraps the <c>qmd</c> CLI binary. Test code uses <see cref="InMemoryQmdBackend"/>.
/// </summary>
public interface IQmdBackend : IAsyncDisposable
{
    /// <summary>
    /// Search for documents matching the query.
    /// </summary>
    /// <param name="query">Natural language or keyword query.</param>
    /// <param name="store">Target store name, or null to search all stores.</param>
    /// <param name="mode">Search mode (keyword, semantic, or hybrid).</param>
    /// <param name="limit">Maximum results to return.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<QmdSearchResult[]> SearchAsync(string query, string? store, QmdSearchMode mode, int limit, CancellationToken ct = default);

    /// <summary>
    /// Retrieve a full document by its unique identifier.
    /// </summary>
    /// <param name="id">Document identifier (store-qualified).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<QmdDocument?> GetDocumentAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// List all configured and available knowledge stores.
    /// </summary>
    Task<QmdStoreInfo[]> GetStoresAsync(CancellationToken ct = default);

    /// <summary>
    /// Trigger a re-index of the specified store (or all stores if null).
    /// </summary>
    /// <param name="store">Store name, or null for all stores.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateIndexAsync(string? store, CancellationToken ct = default);

    /// <summary>
    /// Trigger embedding generation for the specified store (or all stores if null).
    /// Required for semantic and hybrid search modes.
    /// </summary>
    /// <param name="store">Store name, or null for all stores.</param>
    /// <param name="ct">Cancellation token.</param>
    Task EmbedAsync(string? store, CancellationToken ct = default);
}

/// <summary>Search mode for QMD queries.</summary>
public enum QmdSearchMode
{
    /// <summary>BM25 keyword-based search.</summary>
    Keyword,

    /// <summary>Vector/embedding-based semantic search.</summary>
    Semantic,

    /// <summary>Combined keyword + semantic scoring (default).</summary>
    Hybrid
}

/// <summary>A single search result from the QMD backend.</summary>
/// <param name="Id">Unique document identifier.</param>
/// <param name="Store">Name of the store containing this document.</param>
/// <param name="Path">Filesystem path to the source document.</param>
/// <param name="Title">Document title (extracted from frontmatter or filename).</param>
/// <param name="Score">Relevance score (higher is better).</param>
/// <param name="Snippet">Relevant text excerpt from the document.</param>
public sealed record QmdSearchResult(string Id, string Store, string Path, string Title, double Score, string Snippet);

/// <summary>A full document retrieved from the QMD backend.</summary>
/// <param name="Id">Unique document identifier.</param>
/// <param name="Store">Name of the store containing this document.</param>
/// <param name="Path">Filesystem path to the source document.</param>
/// <param name="Title">Document title.</param>
/// <param name="Content">Full document content.</param>
public sealed record QmdDocument(string Id, string Store, string Path, string Title, string Content);

/// <summary>Metadata about a configured QMD knowledge store.</summary>
/// <param name="Name">Store identifier.</param>
/// <param name="Path">Filesystem path to the indexed folder.</param>
/// <param name="Description">Human-readable description (from config).</param>
/// <param name="DocumentCount">Number of indexed documents.</param>
/// <param name="LastUpdated">Timestamp of last successful index update.</param>
/// <param name="Healthy">Whether the store is in a healthy/queryable state.</param>
public sealed record QmdStoreInfo(string Name, string Path, string? Description, int DocumentCount, DateTimeOffset? LastUpdated, bool Healthy);
