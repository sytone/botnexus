namespace BotNexus.Agent.Providers.Core.Registry;

/// <summary>
/// Classification of a <c>(provider, modelId)</c> pair against the <see cref="ModelRegistry"/>.
/// </summary>
public enum ModelPreflightKind
{
    /// <summary>Nothing to check - no model was supplied.</summary>
    NotSpecified,

    /// <summary>
    /// No populated model registry was available, so the pair could not be checked. Treated as
    /// "cannot know", never as a rejection - a host that has not registered any models (minimal
    /// test hosts, early startup) must not start refusing otherwise valid input.
    /// </summary>
    RegistryUnavailable,

    /// <summary>The pair resolved to a concrete registered model.</summary>
    Resolved,

    /// <summary>The named provider is not present in the registry.</summary>
    UnknownProvider,

    /// <summary>The provider is known (or unqualified) but the model id is not registered for it.</summary>
    UnknownModel
}

/// <summary>
/// The structured outcome of a model-registry preflight.
/// </summary>
/// <param name="Kind">The classification.</param>
/// <param name="Provider">The canonical provider when <see cref="ModelPreflightKind.Resolved"/>; otherwise <see langword="null"/>.</param>
/// <param name="ModelId">The canonical model id when <see cref="ModelPreflightKind.Resolved"/>; otherwise <see langword="null"/>.</param>
/// <param name="AvailableProviders">Registered provider keys, ordered - the remedy list for <see cref="ModelPreflightKind.UnknownProvider"/>.</param>
/// <param name="AvailableModels">Registered model ids for the named provider (or provider-qualified ids when the input was unqualified) - the remedy list for <see cref="ModelPreflightKind.UnknownModel"/>.</param>
public readonly record struct ModelPreflightResult(
    ModelPreflightKind Kind,
    string? Provider,
    string? ModelId,
    IReadOnlyList<string> AvailableProviders,
    IReadOnlyList<string> AvailableModels)
{
    /// <summary>
    /// Whether this outcome should block the caller. Only the two "we positively know this does
    /// not exist" kinds are rejections; a missing registry deliberately is not.
    /// </summary>
    public bool IsRejection =>
        Kind is ModelPreflightKind.UnknownProvider or ModelPreflightKind.UnknownModel;
}

/// <summary>
/// The single place in the platform that answers "does this <c>(provider, model)</c> pair name
/// something the runtime can actually resolve?".
/// <para>
/// Two independent surfaces need this answer before they persist anything: cron model overrides
/// (#2373) and the <c>create_agent</c>/<c>update_agent</c> tools (#2649). Both previously risked
/// writing a record that only fails much later, at the point the model is resolved, long after the
/// session that authored it has gone. Keeping the classification here - rather than copied per
/// caller - is what stops the two surfaces drifting into different notions of "valid provider",
/// which is precisely the bug #2649 describes: one namespace written, a different one read.
/// </para>
/// <para>
/// Callers own their own operator-facing wording; this type only classifies and supplies the
/// remedy lists, plus <see cref="FormatList"/> so every message stays inside a sane length budget.
/// </para>
/// </summary>
public static class ModelPreflight
{
    /// <summary>
    /// Classifies an explicitly qualified <paramref name="provider"/> / <paramref name="modelId"/>
    /// pair. Provider aliases understood by <see cref="ModelRegistry"/> (for example
    /// <c>copilot</c>) are honoured, so a successful lookup is authoritative even when
    /// <paramref name="provider"/> is not the canonical key.
    /// </summary>
    /// <param name="registry">The model registry, or <see langword="null"/> when the host has none.</param>
    /// <param name="provider">The provider instance key as authored.</param>
    /// <param name="modelId">The model id as authored.</param>
    /// <returns>The classification; never throws for malformed input.</returns>
    public static ModelPreflightResult Resolve(ModelRegistry? registry, string? provider, string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId) && string.IsNullOrWhiteSpace(provider))
            return new ModelPreflightResult(ModelPreflightKind.NotSpecified, null, null, [], []);

        var providers = registry?.GetProviders() ?? [];
        if (providers.Count == 0)
            return new ModelPreflightResult(ModelPreflightKind.RegistryUnavailable, null, null, [], []);

        var orderedProviders = providers.Order(StringComparer.OrdinalIgnoreCase).ToList();
        var rawProvider = provider?.Trim() ?? string.Empty;
        var rawModel = modelId?.Trim() ?? string.Empty;

        var resolved = registry!.GetModel(rawProvider, rawModel);
        if (resolved is not null)
            return new ModelPreflightResult(ModelPreflightKind.Resolved, resolved.Provider, resolved.Id, orderedProviders, []);

        // Distinguish "the provider does not exist" from "the provider exists but not this model":
        // GetModels also alias-resolves, so a non-empty list means the provider is real.
        var knownModels = registry.GetModels(rawProvider);
        if (knownModels.Count == 0)
            return new ModelPreflightResult(ModelPreflightKind.UnknownProvider, null, null, orderedProviders, []);

        return new ModelPreflightResult(
            ModelPreflightKind.UnknownModel,
            null,
            null,
            orderedProviders,
            knownModels.Select(m => m.Id).Order(StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>
    /// Classifies an unqualified model id by probing every registered provider, for callers whose
    /// input carries no provider (a bare cron <c>model</c> override). On failure the remedy list is
    /// provider-qualified (<c>provider/model</c>) because a bare id alone would not tell the
    /// operator which provider to pair it with.
    /// </summary>
    /// <param name="registry">The model registry, or <see langword="null"/> when the host has none.</param>
    /// <param name="modelId">The bare model id as authored.</param>
    /// <returns>The classification; never throws for malformed input.</returns>
    public static ModelPreflightResult ResolveBare(ModelRegistry? registry, string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return new ModelPreflightResult(ModelPreflightKind.NotSpecified, null, null, [], []);

        var providers = registry?.GetProviders() ?? [];
        if (providers.Count == 0)
            return new ModelPreflightResult(ModelPreflightKind.RegistryUnavailable, null, null, [], []);

        var orderedProviders = providers.Order(StringComparer.OrdinalIgnoreCase).ToList();
        var raw = modelId.Trim();

        foreach (var provider in orderedProviders)
        {
            if (registry!.GetModel(provider, raw) is { } resolved)
                return new ModelPreflightResult(ModelPreflightKind.Resolved, resolved.Provider, resolved.Id, orderedProviders, []);
        }

        var qualified = orderedProviders
            .SelectMany(provider => registry!.GetModels(provider).Select(m => $"{provider}/{m.Id}"))
            .ToList();

        return new ModelPreflightResult(ModelPreflightKind.UnknownModel, null, null, orderedProviders, qualified);
    }

    /// <summary>
    /// Builds <c>"&lt;prefix&gt;a, b, ... (N more)&lt;suffix&gt;"</c> while guaranteeing the whole string stays
    /// within <paramref name="maxLength"/>. The <c>(N more)</c> tail is what tells an operator the
    /// list was elided rather than that the registry only holds a handful of entries.
    /// </summary>
    /// <param name="prefix">Text before the list.</param>
    /// <param name="items">The remedy list.</param>
    /// <param name="suffix">Text after the list.</param>
    /// <param name="maxLength">Hard ceiling for the produced string.</param>
    public static string FormatList(string prefix, IEnumerable<string> items, string suffix, int maxLength)
    {
        var all = items.ToList();
        var builder = new System.Text.StringBuilder(prefix);
        var written = 0;

        foreach (var item in all)
        {
            var candidate = written == 0 ? item : ", " + item;
            // Reserve room for the worst-case elision tail plus the suffix.
            var reserve = $", ... ({all.Count} more)".Length + suffix.Length;
            if (builder.Length + candidate.Length + reserve > maxLength)
                break;

            builder.Append(candidate);
            written++;
        }

        if (written < all.Count)
        {
            if (written > 0)
                builder.Append(", ");
            builder.Append("... (").Append(all.Count - written).Append(" more)");
        }

        builder.Append(suffix);
        var text = builder.ToString();
        if (text.Length <= maxLength)
            return text;

        const string ellipsis = "...";
        return text[..(maxLength - ellipsis.Length)] + ellipsis;
    }
}
