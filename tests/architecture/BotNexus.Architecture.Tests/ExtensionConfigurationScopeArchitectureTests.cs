using System.Text.Json;
using System.Text.RegularExpressions;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Guards the explicit, non-inheriting extension configuration model introduced by #4405.
/// </summary>
public sealed class ExtensionConfigurationScopeArchitectureTests : ArchitectureTest
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly Regex ProductionTypeDeclaration = new(
        @"\b(?:class|record(?:\s+(?:class|struct))?|struct|interface)\s+([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled);

    private static readonly Regex AgentExtensionConfigRead = new(
        @"\b(?:descriptor|context\s*\.\s*Descriptor)\s*\.\s*ExtensionConfig\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DirectJsonDeserialization = new(
        @"(?:JsonSerializer\s*\.\s*Deserialize|\.\s*Deserialize\s*<)",
        RegexOptions.Compiled);

    /// <summary>
    /// Reviewed exceptions for agent-scoped consumers that cannot yet use a sanctioned seam.
    /// Keep this list narrow and explain every entry; an empty list is the desired steady state.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ReviewedAgentConsumerExceptions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
        };

    /// <summary>
    /// No shared production abstraction may imply that independently owned extension scopes are
    /// merged, cascaded, or inherited. Scope selection stays explicit at each consumer.
    /// </summary>
    [Fact]
    public void ProductionHasNoSharedExtensionConfigMergerCascadeOrInheritanceType()
    {
        var offenders = new List<string>();

        foreach (var file in ProductionSourceFiles())
        {
            var text = File.ReadAllText(file);
            foreach (Match match in ProductionTypeDeclaration.Matches(text))
            {
                var typeName = match.Groups[1].Value;
                var namesExtensionConfig =
                    typeName.Contains("ExtensionConfig", StringComparison.OrdinalIgnoreCase) ||
                    typeName.Contains("ExtensionConfiguration", StringComparison.OrdinalIgnoreCase);
                var namesForbiddenComposition =
                    typeName.Contains("Merge", StringComparison.OrdinalIgnoreCase) ||
                    typeName.Contains("Cascade", StringComparison.OrdinalIgnoreCase) ||
                    typeName.Contains("Inherit", StringComparison.OrdinalIgnoreCase);

                if (namesExtensionConfig && namesForbiddenComposition)
                {
                    offenders.Add($"{Relative(file)}: {typeName}");
                }
            }
        }

        offenders.ShouldBeEmpty(
            "Extension configuration scopes are independent. Do not introduce a shared merger, " +
            "cascade, or inheritance production type; consumers must select one explicit scope " +
            "through ExtensionConfigBinder or ExtensionConfigScopeReader (#4405):" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// Agent-scoped extension consumers that inspect an agent descriptor route binding through one
    /// of the sanctioned single-scope seams instead of directly deserializing the raw config bag.
    /// </summary>
    [Fact]
    public void AgentScopedExtensionConsumersUseSanctionedSingleScopeReaders()
    {
        var offenders = new List<string>();

        foreach (var extensionDirectory in AgentScopedExtensionDirectories())
        {
            foreach (var file in Directory.EnumerateFiles(extensionDirectory, "*.cs", SearchOption.AllDirectories))
            {
                if (IsBuildOutput(file))
                {
                    continue;
                }

                var text = File.ReadAllText(file);
                if (!AgentExtensionConfigRead.IsMatch(text))
                {
                    continue;
                }

                var usesSanctionedReader =
                    text.Contains("ExtensionConfigBinder", StringComparison.Ordinal) ||
                    text.Contains("ExtensionConfigScopeReader", StringComparison.Ordinal);
                if (usesSanctionedReader && !DirectJsonDeserialization.IsMatch(text))
                {
                    continue;
                }

                var relative = Relative(file);
                if (ReviewedAgentConsumerExceptions.ContainsKey(relative))
                {
                    continue;
                }

                offenders.Add(relative);
            }
        }

        offenders.ShouldBeEmpty(
            "Agent-scoped extension consumers that read AgentDescriptor.ExtensionConfig must use " +
            "ExtensionConfigBinder or ExtensionConfigScopeReader. Add only a narrow, reviewed " +
            "exception with a reason when migration is genuinely impossible (#4405):" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    private IEnumerable<string> AgentScopedExtensionDirectories()
    {
        var extensionsRoot = Path.Combine(Repository.Root, "src", "extensions");
        foreach (var manifestPath in Directory.EnumerateFiles(
                     extensionsRoot, "botnexus-extension.json", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(manifestPath))
            {
                continue;
            }

            var manifest = JsonSerializer.Deserialize<ManifestScopeRecord>(
                File.ReadAllText(manifestPath), ManifestJsonOptions);
            if (manifest?.ConfigurationScopes?.Contains("agent", StringComparer.OrdinalIgnoreCase) == true)
            {
                yield return Path.GetDirectoryName(manifestPath)!;
            }
        }
    }

    private IEnumerable<string> ProductionSourceFiles()
    {
        foreach (var file in Directory.EnumerateFiles(Repository.SourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (!IsBuildOutput(file))
            {
                yield return file;
            }
        }
    }

    private string Relative(string path) =>
        Path.GetRelativePath(Repository.Root, path).Replace('\\', '/');

    private static bool IsBuildOutput(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment =>
            segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record ManifestScopeRecord
    {
        public IReadOnlyList<string>? ConfigurationScopes { get; init; }
    }
}
