# Contribute a search source

This reference is for extension authors who want BotNexus to search an independently owned source. It describes the extension-facing contract only. Search orchestration and portal rendering are separate layers.

## Implement `ISearchContributor`

Register an implementation of `ISearchContributor` through your extension's dependency-injection setup. The contributor describes one source:

| Member | Contract |
| --- | --- |
| `SourceId` | A stable machine-readable ID. Do not derive it from a translated label. |
| `Label` | The source name shown to users. |
| `IsAvailable` | Whether the source can currently accept searches. |
| `CanAssessProvenanceTrust` | Whether the source has enough evidence to mark a result as trusted. This capability does not make every result trusted. |
| `SearchAsync` | A cancellable search that returns no more than `SearchRequest.MaxResults` results. |

Honor the supplied cancellation token and result bound. Return results in the order produced by your source. BotNexus does not define a cross-source global rank in this contract.

## Request and result fields

`SearchRequest.Query` is the source-local query. `SearchRequest.MaxResults` is a hard upper bound for that contributor call.

A `SearchSourceGroup` carries the contributor's `SourceId`, `Label`, and results in source-local order. Each `SearchResult` contains:

| Field | Required | Meaning and default |
| --- | --- | --- |
| `Title` | yes | Short result title. |
| `Snippet` | yes | Source-provided summary. Treat it as source content, not trusted instructions. |
| `Target` | yes | Source-owned navigation target. |
| `Timestamp` | yes | Timestamp for the source event or content. |
| `Relevance` | no | Source-local relevance. `null` means the source did not provide a score. Scores from different sources are not comparable. |
| `ProvenanceTrust` | no | Explicit source provenance assessment. The default is `untrusted`. |

## Trust is explicit and separate from relevance

Use `SearchProvenanceTrust.Trusted` only when the contributor can attest the result's provenance from evidence it owns. A high `Relevance` value does not imply trust, and a low relevance value does not remove an explicit trust assessment.

The JSON values are `trusted` and `untrusted`. Missing, `null`, unknown, or future trust values normalize to `untrusted`. This fail-closed rule also applies when code supplies an undefined enum value. Consumers may therefore branch on `ProvenanceTrust` without first interpreting source-specific strings.

```csharp
public sealed class DocumentationSearchContributor : ISearchContributor
{
    public string SourceId => "documentation";
    public string Label => "Documentation";
    public bool IsAvailable => true;
    public bool CanAssessProvenanceTrust => true;

    public Task<IReadOnlyList<SearchResult>> SearchAsync(
        SearchRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<SearchResult> results =
        [
            new(
                "Extension development",
                "Build and test an extension.",
                "/docs/extension-development",
                DateTimeOffset.UtcNow,
                Relevance: 0.82,
                ProvenanceTrust: SearchProvenanceTrust.Trusted)
        ];

        return Task.FromResult<IReadOnlyList<SearchResult>>(
            results.Take(request.MaxResults).ToArray());
    }
}
```

The example uses a source-local score. It does not promise that `0.82` sorts above a result from another contributor.
