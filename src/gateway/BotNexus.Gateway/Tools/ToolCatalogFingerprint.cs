using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BotNexus.Agent.Core.Tools;

namespace BotNexus.Gateway.Tools;

/// <summary>
/// An order-sensitive fingerprint of the tool catalogue an agent is about to be given.
/// </summary>
/// <remarks>
/// <para>
/// The tools array is the first segment of every provider's cache prefix, and both Anthropic and
/// OpenAI document tool definitions <em>and their ordering</em> as cache invalidators. A catalogue
/// that reshuffles between requests therefore re-bills the entire cached prefix behind it -- system
/// prompt and whole conversation included -- with no error, no warning, and no symptom other than
/// the invoice.
/// </para>
/// <para>
/// The catalogue is assembled from a registry, a set of tool providers and a set of contributors,
/// several of which reach remote MCP servers whose <c>tools/list</c> order is the server's choice
/// and can change across a restart or a version bump. Rather than assume that composition is
/// stable, log a fingerprint and let a run-to-run change be the evidence.
/// </para>
/// <para>
/// Deliberately covers name, description and schema as well as order: a description edit is just
/// as invalidating as a reordering, and a fingerprint that moved for only one of the two would
/// send someone hunting in the wrong place.
/// </para>
/// </remarks>
public static class ToolCatalogFingerprint
{
    private const char FieldSeparator = '\u001F';
    private const char RecordSeparator = '\u001E';

    /// <summary>
    /// Computes a short, stable fingerprint over the catalogue in the order it will be sent.
    /// </summary>
    /// <param name="tools">The resolved tool catalogue.</param>
    /// <returns>Twelve lowercase hex characters; the same catalogue always yields the same value.</returns>
    public static string Compute(IReadOnlyList<IAgentTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var builder = new StringBuilder();

        foreach (var tool in tools)
        {
            var definition = tool.Definition;
            builder
                .Append(definition.Name)
                .Append(FieldSeparator)
                .Append(definition.Description)
                .Append(FieldSeparator)
                .Append(DescribeSchema(definition.Parameters))
                .Append(RecordSeparator);
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(digest)[..12].ToLowerInvariant();
    }

    /// <summary>
    /// A tool whose schema never got populated carries a default <see cref="JsonElement"/>, and
    /// asking that for its raw text throws. One absent schema must not take down the request the
    /// fingerprint was only observing.
    /// </summary>
    private static string DescribeSchema(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Undefined ? string.Empty : schema.GetRawText();
}
