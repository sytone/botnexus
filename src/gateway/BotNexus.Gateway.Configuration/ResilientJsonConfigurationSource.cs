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
/// <c>reloadOnChange: true</c>, the framework's <see cref="FileConfigurationProvider"/> re-invokes
/// <see cref="FileConfigurationProvider.Load()"/> from the file-watcher change-token callback. On a
/// parse failure that method (a) <em>clears</em> <c>Data</c> and (b) rethrows the failure wrapped in
/// an <see cref="InvalidDataException"/> — on a background thread, which terminates the process.
/// So the startup guard genuinely does not protect the reload path, and neither does
/// <see cref="PlatformConfigPostConfigure"/> (it catches <see cref="System.Text.Json.JsonException"/>,
/// which the provider has already wrapped by then).
/// </para>
/// <para>
/// This provider therefore validates <em>before</em> applying: it snapshots the current data,
/// delegates to the base loader, and on either a load/parse failure or a semantic validation
/// failure restores the snapshot and reports the error through <see cref="OnLoadFailure"/> without
/// rethrowing. Semantic validation reuses the existing startup validation surface
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
    private ConfigDocument? _candidateRawDocument;

    /// <summary>
    /// Loads the file, keeping the last-known-good data when the candidate cannot be parsed or does
    /// not pass platform validation. Never throws for a bad file: the whole point is that a corrupt
    /// <c>config.json</c> must not take down a running host.
    /// </summary>
    public override void Load()
    {
        // Snapshot the currently applied data so a rejected candidate can be rolled back. The base
        // loader assigns an EMPTY dictionary before rethrowing on a reload parse failure, so the
        // snapshot has to be taken up-front.
        var lastKnownGood = new Dictionary<string, string?>(Data, StringComparer.OrdinalIgnoreCase);
        var lastKnownGoodRaw = _acceptedRawDocument;
        _candidateRawDocument = null;

        try
        {
            base.Load();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or FormatException)
        {
            Data = lastKnownGood;
            _acceptedRawDocument = lastKnownGoodRaw;
            Report(
                $"Failed to load configuration file '{_source.Path}'. The previous configuration is being retained; fix the JSON to apply changes.",
                ex);
            return;
        }

        _acceptedRawDocument = _candidateRawDocument;
        if (!_source.ValidatePlatformConfig)
            return;

        var validationError = TryValidateCandidate();
        if (validationError is null)
            return;

        if (lastKnownGoodRaw is null)
        {
            // There is no healthy state to roll back to during the first load. Keep the exact
            // invalid candidate in the provider so explicit options access produces the existing,
            // actionable OptionsValidationException instead of silently materialising defaults.
            // Load itself still does not throw, so host composition can choose degraded startup.
            Report(
                $"Configuration file '{_source.Path}' failed initial validation. The invalid configuration remains available for controlled options validation. {validationError}",
                null);
            return;
        }

        Data = lastKnownGood;
        _acceptedRawDocument = lastKnownGoodRaw;
        Report(
            $"Configuration file '{_source.Path}' failed validation and was rejected. The previous configuration is being retained. {validationError}",
            null);
    }

    /// <summary>
    /// Binds the freshly loaded data to <see cref="PlatformConfig"/> exactly the way the options
    /// pipeline does (bind, then <see cref="PlatformConfigPostConfigure"/> normalisation) and runs
    /// the existing <see cref="PlatformConfigOptionsValidator"/> over it. Returns the joined error
    /// text when the candidate must be rejected, or <see langword="null"/> when it is acceptable.
    /// </summary>
    public override void Load(Stream stream)
    {
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        var bytes = copy.ToArray();
        _candidateRawDocument = ConfigDocument.Parse(System.Text.Encoding.UTF8.GetString(bytes));
        base.Load(new MemoryStream(bytes));
    }

    ConfigDocument? IAcceptedRawConfigDocumentProvider.GetAcceptedRawDocument()
        => _acceptedRawDocument?.DeepClone();

    private string? TryValidateCandidate()
    {
        try
        {
            var candidate = _candidateRawDocument is null
                ? new PlatformConfig()
                : PlatformConfigLoader.MaterializeConfig(_candidateRawDocument.ToJsonString());
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
