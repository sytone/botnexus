using System.Reflection;
using System.Text.Json;
using NJsonSchema;
using NJsonSchema.Validation;

namespace BotNexus.Extensions.Plugins;

/// <summary>
/// Parses and validates plugin manifests and marketplace catalogs against the embedded
/// JSON Schema files. The schema files are the single source of truth for field names and
/// requiredness - this class deliberately contains no hand-written field list, mirroring how
/// <c>issue-schema.json</c> is the single source for the issue linter. Malformed documents are
/// rejected with a message naming the offending field; no best-effort coercion is attempted,
/// because guessing at an unknown shape silently installs something the author did not write.
/// </summary>
public sealed class PluginManifestParser
{
    /// <summary>Conventional directory inside a plugin root holding its metadata.</summary>
    public const string PluginMetadataDirectoryName = ".botnexus-plugin";

    /// <summary>Conventional manifest file name inside <see cref="PluginMetadataDirectoryName"/>.</summary>
    public const string ManifestFileName = "plugin.json";

    private const string ManifestSchemaResource = "BotNexus.Extensions.Plugins.Schemas.plugin-manifest.schema.json";
    private const string MarketplaceSchemaResource = "BotNexus.Extensions.Plugins.Schemas.marketplace.schema.json";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly Lazy<JsonSchema> _manifestSchema =
        new(() => LoadSchema(ManifestSchemaResource), isThreadSafe: true);

    private readonly Lazy<JsonSchema> _marketplaceSchema =
        new(() => LoadSchema(MarketplaceSchemaResource), isThreadSafe: true);

    /// <summary>
    /// Raw text of the manifest schema exactly as it is checked into the repository. Exposed so
    /// tests can assert the parser and the schema file agree on the required-field set and
    /// therefore cannot drift apart.
    /// </summary>
    public static string ManifestSchemaJson => ReadResource(ManifestSchemaResource);

    /// <summary>
    /// Raw text of the marketplace schema exactly as it is checked into the repository.
    /// </summary>
    public static string MarketplaceSchemaJson => ReadResource(MarketplaceSchemaResource);

    /// <summary>
    /// Field names the manifest schema declares as required, read from the schema at runtime.
    /// A caller building a manifest template uses this so the template can never omit a field
    /// the schema demands.
    /// </summary>
    public IReadOnlyCollection<string> RequiredManifestFields =>
        [.. _manifestSchema.Value.RequiredProperties.OrderBy(static f => f, StringComparer.Ordinal)];

    /// <summary>
    /// Field names the marketplace schema declares as required, read from the schema at runtime.
    /// </summary>
    public IReadOnlyCollection<string> RequiredMarketplaceFields =>
        [.. _marketplaceSchema.Value.RequiredProperties.OrderBy(static f => f, StringComparer.Ordinal)];

    /// <summary>
    /// Validates manifest JSON against the schema and projects it to a typed manifest.
    /// Returns a failure result naming the offending field rather than throwing, so a directory
    /// scan can report every bad plugin it finds instead of aborting on the first.
    /// </summary>
    /// <param name="json">Raw manifest document text.</param>
    public PluginParseResult<PluginManifest> ParseManifest(string json) =>
        Parse<PluginManifest>(json, _manifestSchema.Value, "manifest");

    /// <summary>
    /// Validates marketplace catalog JSON against the schema and projects it to a typed catalog.
    /// </summary>
    /// <param name="json">Raw catalog document text.</param>
    public PluginParseResult<MarketplaceCatalog> ParseMarketplace(string json) =>
        Parse<MarketplaceCatalog>(json, _marketplaceSchema.Value, "marketplace");

    /// <summary>
    /// Resolves <c>.botnexus-plugin/plugin.json</c> under <paramref name="pluginDirectory"/> and
    /// validates it. A missing manifest is a failure rather than an empty success, because a
    /// directory without a manifest is not a plugin and should not be treated as one.
    /// </summary>
    /// <param name="pluginDirectory">Root directory of the candidate plugin.</param>
    public PluginParseResult<PluginManifest> ParsePluginDirectory(string pluginDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);

        var manifestPath = Path.Combine(pluginDirectory, PluginMetadataDirectoryName, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return PluginParseResult<PluginManifest>.Failure(
                PluginMetadataDirectoryName + "/" + ManifestFileName,
                $"Plugin manifest not found: expected '{PluginMetadataDirectoryName}/{ManifestFileName}' under '{pluginDirectory}'.");
        }

        return ParseManifest(File.ReadAllText(manifestPath));
    }

    private static PluginParseResult<T> Parse<T>(string json, JsonSchema schema, string documentKind)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return PluginParseResult<T>.Failure("#", $"The plugin {documentKind} document is empty.");
        }

        // Check JSON well-formedness up front: NJsonSchema surfaces syntax failures as a
        // Newtonsoft reader exception, which would otherwise escape as an unhandled throw.
        try
        {
            using var probe = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return PluginParseResult<T>.Failure("#", $"The plugin {documentKind} document is not valid JSON: {ex.Message}");
        }

        var validationErrors = schema.Validate(json);
        if (validationErrors.Count > 0)
        {
            var errors = Flatten(validationErrors)
                .Select(e => new PluginValidationError(
                    string.IsNullOrEmpty(e.Path) ? "#" : e.Path,
                    $"Plugin {documentKind} field '{DescribeField(e)}' is invalid: {e.Kind}."))
                .ToList();
            return PluginParseResult<T>.Failure(errors);
        }

        T? value;
        try
        {
            value = JsonSerializer.Deserialize<T>(json, SerializerOptions);
        }
        catch (JsonException ex)
        {
            return PluginParseResult<T>.Failure("#", $"The plugin {documentKind} document is not valid JSON: {ex.Message}");
        }

        return value is null
            ? PluginParseResult<T>.Failure("#", $"The plugin {documentKind} document deserialised to null.")
            : PluginParseResult<T>.Success(value);
    }

    // Array-item and sub-schema failures nest the real cause one or more levels down. Without
    // flattening, a caller only ever sees "ArrayItemNotValid" and never learns which field
    // inside the item is wrong - which defeats the point of naming the offending field.
    private static IEnumerable<ValidationError> Flatten(IEnumerable<ValidationError> errors)
    {
        foreach (var error in errors)
        {
            List<ValidationError> children = error is ChildSchemaValidationError child
                ? [.. child.Errors.SelectMany(static kvp => kvp.Value)]
                : [];

            if (children.Count == 0)
            {
                yield return error;
                continue;
            }

            foreach (var nested in Flatten(children))
            {
                yield return nested;
            }
        }
    }

    // A missing-property error reports the parent object's path, so the property name that is
    // actually at fault lives on Property. Prefer it when present so the message names the field.
    private static string DescribeField(ValidationError error) =>
        !string.IsNullOrEmpty(error.Property)
            ? error.Property
            : string.IsNullOrEmpty(error.Path) ? "#" : error.Path;

    private static JsonSchema LoadSchema(string resourceName) =>
        JsonSchema.FromJsonAsync(ReadResource(resourceName)).GetAwaiter().GetResult();

    private static string ReadResource(string resourceName)
    {
        using var stream = typeof(PluginManifestParser).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded schema '{resourceName}' is missing from {typeof(PluginManifestParser).Assembly.GetName().Name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
