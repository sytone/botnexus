namespace BotNexus.SourceGenerators;

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

/// <summary>
/// Roslyn incremental generator that turns <c>feature-flags.json</c> into the platform's feature
/// flag inventory (#2769).
/// <para>
/// Before this generator the single declared flag was a <c>const string</c> duplicated across two
/// files with nothing binding them; a rename in one was silently unobserved by the other, and a
/// misspelling at a call site evaluated as absent and returned the default. Generating the
/// inventory removes all three failure modes by construction rather than by review convention.
/// </para>
/// <para>
/// <b>Failures are build errors, not silence.</b> A malformed or unparseable file reports
/// <c>BNFF001</c> and emits nothing further. This is a deliberate divergence from the Oro reference
/// generator, which swallows parse errors so a broken file merely produces no code: that choice
/// turns one bad character into a cascade of "FeatureFlags does not exist" errors pointing at
/// innocent call sites, and leaves the actual cause off the build log entirely.
/// </para>
/// <para>
/// <b>Incremental caching</b> depends on <see cref="GeneratorOptions"/> implementing
/// <see cref="IEquatable{T}"/> correctly and on the emitted source being independent of the
/// ambient clock. Age-dependent judgements are therefore reported as diagnostics
/// (<c>BNFF003</c>), never baked into generated code - a generated file whose content depends on
/// today's date invalidates the cache daily and makes builds irreproducible.
/// </para>
/// </summary>
[Generator]
public sealed class FeatureFlagSourceGenerator : IIncrementalGenerator
{
    /// <summary>The file name this generator consumes from <c>AdditionalFiles</c>.</summary>
    public const string FlagsFileName = "feature-flags.json";

    /// <summary>Diagnostic ID reported when <c>feature-flags.json</c> cannot be parsed.</summary>
    public const string ParseErrorId = "BNFF001";

    /// <summary>Diagnostic ID reported when code emission fails after a successful parse.</summary>
    public const string GenerationErrorId = "BNFF002";

    /// <summary>Diagnostic ID reported for a flag older than the configured age threshold.</summary>
    public const string StaleFlagId = "BNFF003";

    private static readonly DiagnosticDescriptor ParseError = new(
        ParseErrorId,
        "Feature flag inventory parse error",
        "Failed to parse " + FlagsFileName + ": {0}",
        "FeatureFlags",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The feature flag inventory could not be read, so no flags were generated. "
            + "Fix the JSON rather than the resulting call-site errors.");

    private static readonly DiagnosticDescriptor GenerationError = new(
        GenerationErrorId,
        "Feature flag inventory generation error",
        "Failed to generate the feature flag inventory: {0}",
        "FeatureFlags",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // Warning, not error: a stale flag is a prompt to its owner to retire or renew it, and must not
    // stop the build of an unrelated change. TreatWarningsAsErrors is on repo-wide, so this DOES
    // fail CI - which is the intended pressure, and why `ignoreFlagAge` exists as the explicit
    // "this flag is meant to endure" escape hatch rather than a silent exception list.
    private static readonly DiagnosticDescriptor StaleFlag = new(
        StaleFlagId,
        "Feature flag is stale",
        "Feature flag '{0}' (owner: {1}) was added {2} days ago, past the {3}-day threshold. "
            + "Retire it and remove the gated code, or set \"ignoreFlagAge\": true if it is meant to endure.",
        "FeatureFlags",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A flag nobody removed is indistinguishable from a flag still in use. "
            + "The age threshold forces that question to be answered rather than deferred.");

    /// <summary>
    /// Wires the pipeline: read <c>feature-flags.json</c> from <c>AdditionalFiles</c>, combine it
    /// with the MSBuild-visible properties, and emit one source file.
    /// <para>
    /// Changing the pipeline shape (adding or removing <c>Combine</c>/<c>Select</c>/<c>Where</c>
    /// steps) can break incremental caching and degrade IDE responsiveness, because Roslyn
    /// re-runs every step whose inputs it can no longer prove unchanged.
    /// </para>
    /// </summary>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var flagFiles = context.AdditionalTextsProvider
            .Where(static file => file.Path.EndsWith(FlagsFileName, StringComparison.OrdinalIgnoreCase));

        var optionsProvider = context.AnalyzerConfigOptionsProvider
            .Select(static (provider, _) => ExtractOptions(provider));

        var combined = flagFiles
            .Combine(optionsProvider)
            .Select(static (pair, cancellationToken) =>
            {
                var (file, options) = pair;
                var content = file.GetText(cancellationToken)?.ToString();
                var (flags, error) = ParseFlags(content);
                return (Flags: flags, Options: options, Error: error);
            });

        context.RegisterSourceOutput(combined, static (production, source) =>
        {
            if (source.Error is not null)
            {
                production.ReportDiagnostic(Diagnostic.Create(ParseError, Location.None, source.Error));
                return;
            }

            try
            {
                production.AddSource(
                    $"{source.Options.ClassName}.g.cs",
                    FeatureFlagCodeGenerator.GenerateInventory(source.Flags, source.Options));

                ReportStaleFlags(production, source.Flags, source.Options);
            }
            catch (ArgumentException ex)
            {
                production.ReportDiagnostic(Diagnostic.Create(GenerationError, Location.None, ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                production.ReportDiagnostic(Diagnostic.Create(GenerationError, Location.None, ex.Message));
            }
        });
    }

    /// <summary>
    /// Reads the MSBuild-visible properties. Public rather than private so option extraction is
    /// pinned directly by tests rather than only inferred from a full compilation.
    /// </summary>
    public static GeneratorOptions ExtractOptions(AnalyzerConfigOptionsProvider provider)
    {
        var options = new GeneratorOptions();

        if (provider.GlobalOptions.TryGetValue("build_property.FeatureFlagSourceGenerator_Namespace", out var ns)
            && !string.IsNullOrWhiteSpace(ns))
        {
            options.Namespace = ns;
        }

        if (provider.GlobalOptions.TryGetValue("build_property.FeatureFlagSourceGenerator_ClassName", out var className)
            && !string.IsNullOrWhiteSpace(className))
        {
            options.ClassName = className;
        }

        if (provider.GlobalOptions.TryGetValue("build_property.FeatureFlagSourceGenerator_AgeWarning", out var age)
            && int.TryParse(age, out var ageDays)
            && ageDays >= 0)
        {
            options.AgeWarningDays = ageDays;
        }

        return options;
    }

    /// <summary>
    /// Parses the file content, converting any failure into a message for <c>BNFF001</c>. An empty
    /// or whitespace-only file is a parse error rather than an empty inventory: a truncated file
    /// is far more likely than a deliberate declaration that the platform has no flags.
    /// </summary>
    public static (List<FeatureFlagDefinitionModel> Flags, string Error) ParseFlags(string content)
    {
        try
        {
            return (FeatureFlagJsonParser.ParseJson(content), null);
        }
        catch (ArgumentException ex)
        {
            return (new List<FeatureFlagDefinitionModel>(), ex.Message);
        }
    }

    /// <summary>
    /// Reports <c>BNFF003</c> for each live flag past the age threshold. Retired flags are exempt -
    /// they already carry <c>[Obsolete]</c>, so warning about their age as well would be a second
    /// warning for a decision that has been made.
    /// <para>
    /// <paramref name="today"/> is a parameter rather than a read of the ambient clock so the rule
    /// is testable at a fixed instant; a staleness test that depended on the real date would start
    /// passing or failing on its own schedule.
    /// </para>
    /// </summary>
    public static IReadOnlyList<Diagnostic> BuildStaleDiagnostics(
        IEnumerable<FeatureFlagDefinitionModel> flags,
        GeneratorOptions options,
        DateTime today)
    {
        if (options.AgeWarningDays <= 0)
        {
            return Array.Empty<Diagnostic>();
        }

        return flags
            .Where(flag => !flag.IgnoreFlagAge && !flag.DateRetired.HasValue)
            .Select(flag => new { flag, age = (int)(today.Date - flag.DateAdded.Date).TotalDays })
            .Where(entry => entry.age > options.AgeWarningDays)
            .Select(entry => Diagnostic.Create(
                StaleFlag,
                Location.None,
                entry.flag.FeatureName,
                entry.flag.Owner,
                entry.age,
                options.AgeWarningDays))
            .ToList();
    }

    private static void ReportStaleFlags(
        SourceProductionContext production,
        List<FeatureFlagDefinitionModel> flags,
        GeneratorOptions options)
    {
        foreach (var diagnostic in BuildStaleDiagnostics(flags, options, DateTime.UtcNow))
        {
            production.ReportDiagnostic(diagnostic);
        }
    }
}
