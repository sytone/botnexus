using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace BotNexus.Cli.Commands;

// Package identity, contract schema, and lifecycle capabilities are deliberately independent.
internal sealed record UpdaterDescriptor(string PackageVersion, string Revision, int[] SupportedSchemas,
    string[] SupportedCapabilities, string[] RequiredTargetCapabilities);

internal static class UpdaterCompatibility
{
    internal const string ContractPath = "updater-compatibility.json";
    internal const string FirstContractAwareVersion = "0.50.2";
    private static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal static UpdaterDescriptor Running
    {
        get
        {
            var assembly = typeof(UpdaterCompatibility).Assembly;
            var identity = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
            var parts = identity.Split('+', 2);
            using var stream = assembly.GetManifestResourceStream("BotNexus.UpdaterCompatibility");
            using var reader = stream is null ? null : new StreamReader(stream);
            return ReadSupport(reader?.ReadToEnd(), parts[0], parts.Length > 1 ? parts[1] : "unknown");
        }
    }

    internal static UpdaterDescriptor ReadSupport(string? json, string packageVersion, string revision)
    {
        var invalid = new UpdaterDescriptor(packageVersion, revision, [], [], []);
        if (string.IsNullOrWhiteSpace(json)) return invalid;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!HasUniqueObjectProperties(document.RootElement)) return invalid;
            var support = JsonSerializer.Deserialize<SupportContract>(json, Options);
            if (support?.SupportedContractSchemas is not { Length: > 0 } schemas
                || schemas.Any(schema => schema <= 0) || schemas.Distinct().Count() != schemas.Length
                || !ValidCapabilities(support.SupportedUpdaterCapabilities) || !ValidCapabilities(support.RequiredTargetCapabilities))
                return invalid;
            return new UpdaterDescriptor(packageVersion, revision, schemas,
                support.SupportedUpdaterCapabilities, support.RequiredTargetCapabilities);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return invalid;
        }
    }

    internal static string? Evaluate(UpdaterDescriptor cli, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "Target compatibility metadata is missing or unreadable (legacy targets are unsupported).";
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return "Malformed target compatibility contract: expected an object.";
            // Reject duplicate keys rather than depending on last-key-wins deserialization.
            if (!HasUniqueObjectProperties(document.RootElement))
                return "Malformed target compatibility contract: duplicate properties.";
            if (!document.RootElement.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var schemaVersion))
                return "Malformed target compatibility contract: schemaVersion is required.";
            if (!cli.SupportedSchemas.Contains(schemaVersion))
                return $"Unsupported compatibility schema {schemaVersion}; running CLI supports [{string.Join(", ", cli.SupportedSchemas)}].";
            var contract = JsonSerializer.Deserialize<Contract>(json, Options);
            if (contract is null || !TryVersion(contract.MinimumUpdaterVersion, out var minimum)
                || minimum < Version.Parse(FirstContractAwareVersion)
                || !ValidCapabilities(contract.RequiredUpdaterCapabilities) || !ValidCapabilities(contract.ProvidedTargetCapabilities))
                return "Malformed target compatibility contract: required version, schemas, or capabilities are invalid.";
            if (!TryVersion(cli.PackageVersion, out var running) || running < minimum)
                return $"Target requires updater >= {contract.MinimumUpdaterVersion}; running package is {cli.PackageVersion}.";
            var unsupported = contract.RequiredUpdaterCapabilities.Except(cli.SupportedCapabilities, StringComparer.Ordinal).ToArray();
            if (unsupported.Length > 0)
                return $"Target requires unsupported updater lifecycle behavior: {string.Join(", ", unsupported)}.";
            var missing = cli.RequiredTargetCapabilities.Except(contract.ProvidedTargetCapabilities, StringComparer.Ordinal).ToArray();
            if (missing.Length > 0)
                return $"Running updater requires target lifecycle behavior not provided by this target: {string.Join(", ", missing)}.";
            return null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return "Malformed target compatibility contract: " + ex.Message;
        }
    }

    internal static string QuoteSource(string path, bool powershell)
        => "'" + path.Replace("'", powershell ? "''" : "'\"'\"'", StringComparison.Ordinal) + "'";

    internal static string ReadPackageVersion(string? props)
    {
        try { return props is null ? "unknown" : XDocument.Parse(props).Descendants("Version").FirstOrDefault()?.Value ?? "unknown"; }
        catch (System.Xml.XmlException) { return "unknown"; }
    }

    private static bool TryVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (text is null || !Version.TryParse(text.Split('+', 2)[0], out var parsed))
            return false;
        version = parsed;
        return true;
    }

    private static bool ValidCapabilities(string[]? capabilities)
        => capabilities is { Length: > 0 } && capabilities.All(c => !string.IsNullOrWhiteSpace(c))
            && capabilities.Distinct(StringComparer.Ordinal).Count() == capabilities.Length;

    private static bool HasUniqueObjectProperties(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var names = element.EnumerateObject().Select(property => property.Name).ToArray();
        return names.Distinct(StringComparer.Ordinal).Count() == names.Length;
    }

    private sealed class Contract
    {
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; }
        [JsonPropertyName("minimumUpdaterVersion")] public string? MinimumUpdaterVersion { get; init; }
        [JsonPropertyName("requiredUpdaterCapabilities")] public string[] RequiredUpdaterCapabilities { get; init; } = [];
        [JsonPropertyName("providedTargetCapabilities")] public string[] ProvidedTargetCapabilities { get; init; } = [];
    }

    private sealed class SupportContract
    {
        [JsonPropertyName("supportedContractSchemas")] public int[] SupportedContractSchemas { get; init; } = [];
        [JsonPropertyName("supportedUpdaterCapabilities")] public string[] SupportedUpdaterCapabilities { get; init; } = [];
        [JsonPropertyName("requiredTargetCapabilities")] public string[] RequiredTargetCapabilities { get; init; } = [];
    }
}
