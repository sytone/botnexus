using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// A <see cref="JsonConfigurationSource"/> whose provider makes <c>config.json</c> reloads
/// <em>fail-safe</em>: a malformed or semantically invalid file is rejected and the previously
/// loaded (last-known-good) configuration data is retained, instead of the parse failure escaping
/// into the host (#2358).
/// </summary>
/// <remarks>
/// <para>
/// The startup guard in <c>BotNexus.Gateway.Api/Program.cs</c> (<c>IsValidJsonFile</c>) only runs
/// once, before the source is added to the pipeline. Once registered with
/// <c>reloadOnChange: true</c>, the framework's <see cref="FileConfigurationProvider"/> invokes its
/// private reload loader, which calls <see cref="FileConfigurationProvider.Load(Stream)"/> but
/// bypasses the public parameterless override. With the stock JSON stream loader, a parse failure
/// causes the private loader to clear <c>Data</c> and rethrow an <see cref="InvalidDataException"/>
/// on the watcher thread. The startup guard and options post-configuration cannot protect that
/// boundary. This provider's stream override instead consumes content failures before they reach
/// that framework failure path.
/// </para>
/// <para>
/// Initial, explicit and watcher loads of <c>config.json</c> share the stream-loading acceptance hook, which parses and
/// validates candidates and retains last-known-good data on rejection. It accepts both binding data
/// and the raw document before returning
/// to the framework, which then notifies consumers. Failures are reported through
/// <see cref="OnLoadFailure"/> without rethrowing. Semantic validation reuses the existing startup validation surface
/// (<see cref="PlatformConfigPostConfigure"/> normalisation followed by
/// <see cref="PlatformConfigOptionsValidator"/>, which itself calls
/// <see cref="PlatformConfigLoader.ValidateAnnotated"/> — the recursive graph walker added by
/// #2276/#2061 — plus <see cref="PlatformConfigSchema"/>); no second, divergent validator is
/// introduced here.
/// </para>
/// </remarks>
public sealed class ResilientJsonConfigurationSource : JsonConfigurationSource
{
    /// <summary>
    /// Invoked with a human-readable reason whenever a reload is rejected and the previous
    /// configuration is retained. The optional exception is the underlying load failure (null for a
    /// semantic validation rejection).
    /// </summary>
    public Action<string, Exception?>? OnLoadFailure { get; init; }

    /// <summary>
    /// When <see langword="true"/> (the default) the candidate data is bound to
    /// <see cref="PlatformConfig"/> and run through the existing platform validation before being
    /// applied. Syntactic (parse) protection is always active regardless of this flag.
    /// </summary>
    public bool ValidatePlatformConfig { get; init; } = true;

    /// <inheritdoc />
    public override IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        // Mirrors what AddJsonFile does for a rooted path: split the absolute path into a physical
        // file provider over its directory plus a relative file name. Without this the file provider
        // defaults to the builder base path and never finds the file, so Data stays empty.
        ResolveFileProvider();
        EnsureDefaults(builder);
        return new ResilientJsonConfigurationProvider(this);
    }
}

/// <summary>
/// The fail-safe provider behind <see cref="ResilientJsonConfigurationSource"/>. See that type for
/// the rationale (#2358).
/// </summary>
internal sealed class ResilientJsonConfigurationProvider(ResilientJsonConfigurationSource source)
    : JsonConfigurationProvider(source), IAcceptedRawConfigDocumentProvider
{
    private readonly ResilientJsonConfigurationSource _source = source;
    private ConfigDocument? _acceptedRawDocument;

    /// <summary>
    /// Retains the current configuration on explicit file-load failures. Watcher loads bypass this
    /// override; parse protection and acceptance belong in the shared stream hook below.
    /// </summary>
    public override void Load()
    {
        var lastKnownGood = Data;
        try
        {
            base.Load();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or FormatException)
        {
            Data = lastKnownGood;
            ReportLoadFailure(ex);
        }
    }

    /// <summary>
    /// Parses, validates and accepts both representations before the framework raises OnReload.
    /// Handling bad content here also prevents its private watcher loader from clearing Data and
    /// rethrowing on the watcher thread (#2358, #4919).
    /// </summary>
    public override void Load(Stream stream)
    {
        var lastKnownGood = Data;
        var lastKnownGoodRaw = _acceptedRawDocument;
        ConfigDocument candidate;
        try
        {
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            var json = reader.ReadToEnd();
            try
            {
                candidate = ConfigDocument.Parse(json);
            }
            catch (InvalidOperationException ex)
            {
                // JsonNode.AsObject rejects valid JSON with a non-object root. Handle only
                // that parse boundary, not unrelated binding or validation operations.
                Data = lastKnownGood;
                ReportLoadFailure(ex);
                return;
            }
            using var bindingStream = ExactModelCapacityConfiguration.CreateBindingStream(candidate);
            base.Load(bindingStream);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or IOException or FormatException)
        {
            Data = lastKnownGood;
            ReportLoadFailure(ex);
            return;
        }

        var validationError = _source.ValidatePlatformConfig ? TryValidateCandidate(candidate) : null;
        if (validationError is not null && lastKnownGoodRaw is not null)
        {
            Data = lastKnownGood;
            Report(
                $"Configuration file '{_source.Path}' failed validation and was rejected. The previous configuration is being retained. {validationError}",
                null);
            return;
        }

        // The raw shape must agree with Data when options callbacks synchronously bind and
        // post-configure during the framework's ensuing OnReload, not after base.Load() returns.
        _acceptedRawDocument = candidate;
        if (validationError is not null)
        {
            // Preserve the existing degraded-startup contract: without a previous document,
            // retain the invalid initial candidate for actionable explicit options validation.
            Report(
                $"Configuration file '{_source.Path}' failed initial validation. The invalid configuration remains available for controlled options validation. {validationError}",
                null);
        }
    }

    private void ReportLoadFailure(Exception exception)
        => Report(
            $"Failed to load configuration file '{_source.Path}'. The previous configuration is being retained; fix the JSON to apply changes.",
            exception);

    ConfigDocument? IAcceptedRawConfigDocumentProvider.GetAcceptedRawDocument()
        => _acceptedRawDocument?.DeepClone();

    private static string? TryValidateCandidate(ConfigDocument document)
    {
        try
        {
            var candidate = PlatformConfigLoader.MaterializeConfig(document.ToJsonString());
            var result = new PlatformConfigOptionsValidator().Validate(Options.DefaultName, candidate);
            return result.Failed
                ? string.Join("; ", result.Failures ?? [])
                : null;
        }
        catch (Exception ex)
        {
            // A validation pass that itself blows up must not be more destructive than the problem
            // it guards against: reject the candidate and keep the last-known-good configuration.
            return $"Validation could not be completed: {ex.Message}";
        }
    }

    private void Report(string message, Exception? exception)
        => _source.OnLoadFailure?.Invoke(message, exception);
}

/// <summary>
/// Registration helpers for <see cref="ResilientJsonConfigurationSource"/> (#2358).
/// </summary>
public static class ResilientJsonConfigurationExtensions
{
    /// <summary>
    /// Adds <paramref name="path"/> as a JSON configuration source that keeps the last-known-good
    /// configuration when a reload produces a malformed or invalid file, rather than letting the
    /// parse failure escape on the file-watcher thread and crash the host.
    /// </summary>
    /// <param name="builder">The configuration builder.</param>
    /// <param name="path">Absolute path to the JSON file.</param>
    /// <param name="optional">Whether a missing file is tolerated.</param>
    /// <param name="reloadOnChange">Whether the file is watched for changes.</param>
    /// <param name="onLoadFailure">Callback invoked when a reload is rejected.</param>
    /// <param name="validatePlatformConfig">Whether to additionally run platform validation before applying.</param>
    public static IConfigurationBuilder AddResilientJsonFile(
        this IConfigurationBuilder builder,
        string path,
        bool optional = true,
        bool reloadOnChange = true,
        Action<string, Exception?>? onLoadFailure = null,
        bool validatePlatformConfig = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var source = new ResilientJsonConfigurationSource
        {
            Path = path,
            Optional = optional,
            ReloadOnChange = reloadOnChange,
            OnLoadFailure = onLoadFailure,
            ValidatePlatformConfig = validatePlatformConfig,
        };

        builder.Add(source);
        return builder;
    }
}
