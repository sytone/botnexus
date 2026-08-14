using System.Text.Json;

namespace BotNexus.Extensions.Qmd.Tests;

/// <summary>
/// #3171: <c>KnowledgeGetTool</c> bounded its 50K-character document preview with a raw UTF-16
/// range slice, which can cut between a high and a low surrogate and emit a lone surrogate into
/// the tool result - and from there into the provider request body. Knowledge documents are
/// arbitrary user markdown and routinely contain emoji, so the boundary is reachable in practice.
/// </summary>
public sealed class KnowledgeGetToolSurrogateSafetyTests
{
    /// <summary>
    /// The tool's own <c>MaxContentChars</c>. Mirrored rather than exposed: the constant is an
    /// implementation detail, and a test that reached in to read it would pass even if the
    /// production value changed underneath the fixture.
    /// </summary>
    private const int MaxContentChars = 50_000;

    /// <summary>U+1F600 GRINNING FACE - two UTF-16 code units, the smallest astral test case.</summary>
    private const string Grinning = "\U0001F600";

    [Fact]
    public async Task Execute_AstralCharacterStraddlingTheLimit_EmitsNoLoneSurrogate()
    {
        // The emoji starts at index MaxContentChars - 1, so a raw slice at MaxContentChars keeps
        // its high surrogate and drops its low surrogate. This is the exact defect shape.
        var content = new string('a', MaxContentChars - 1) + Grinning + new string('b', 100);
        content[MaxContentChars - 1].ShouldSatisfyAllConditions(
            () => char.IsHighSurrogate(content[MaxContentChars - 1]).ShouldBeTrue(),
            () => char.IsLowSurrogate(content[MaxContentChars]).ShouldBeTrue());

        var text = await GetContentAsync(content);

        HasUnpairedSurrogate(text).ShouldBeFalse(
            "#3171: the truncated document preview must not contain a lone surrogate.");
        text.Length.ShouldBeLessThan(content.Length);
        text.ShouldContain("[truncated");
    }

    [Fact]
    public async Task Execute_ContentAtTheLimit_IsReturnedUnchangedWithNoTruncationMarker()
    {
        var content = new string('a', MaxContentChars);

        var text = await GetContentAsync(content);

        text.ShouldBe(content);
        text.ShouldNotContain("[truncated");
    }

    [Fact]
    public async Task Execute_ContentUnderTheLimit_IsReturnedUnchangedWithNoTruncationMarker()
    {
        var content = "# Title\n\nA short document with an emoji " + Grinning + ".";

        var text = await GetContentAsync(content);

        text.ShouldBe(content);
        text.ShouldNotContain("[truncated");
    }

    /// <summary>
    /// Runs the tool over a single document and returns the deserialized <c>content</c> field, so
    /// the assertions run against what the model actually receives rather than an internal value.
    /// </summary>
    private static async Task<string> GetContentAsync(string content)
    {
        var backend = new InMemoryQmdBackend();
        backend.Documents.Add(new QmdDocument(
            "#doc1", "vault", "vault/doc.md", "Doc", content));
        var config = new QmdConfig
        {
            Stores = [new QmdStoreConfig { Name = "vault", Path = "/docs/vault" }]
        };
        var tool = new KnowledgeGetTool(backend, config);

        var result = await tool.ExecuteAsync(
            "tc1", new Dictionary<string, object?> { ["id"] = "#doc1" });

        using var json = JsonDocument.Parse(result.Content[0].Value!);
        return json.RootElement.GetProperty("content").GetString()!;
    }

    /// <summary>
    /// Scans for a surrogate that is not part of a well-formed pair. This is the direct expression
    /// of the invariant; <c>string.IsNormalized</c> and friends do not detect it.
    /// </summary>
    private static bool HasUnpairedSurrogate(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                    return true;
                i++;
                continue;
            }

            if (char.IsLowSurrogate(value[i]))
                return true;
        }

        return false;
    }
}
