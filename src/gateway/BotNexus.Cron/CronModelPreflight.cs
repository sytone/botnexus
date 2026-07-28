using System.Text;
using System.Text.RegularExpressions;
using BotNexus.Agent.Providers.Core.Registry;

namespace BotNexus.Cron;

/// <summary>
/// Classification of a cron job's <c>Model</c> override, produced by
/// <see cref="CronModelPreflight.Resolve"/>.
/// </summary>
public enum CronModelPreflightKind
{
    /// <summary>No override was supplied; the agent's configured model applies.</summary>
    NotSpecified,

    /// <summary>
    /// No populated model registry was available, so the override could not be checked.
    /// Treated as "cannot know", never as a rejection - a host that has not registered models
    /// (minimal test hosts, early startup) must not start refusing valid cron jobs.
    /// </summary>
    RegistryUnavailable,

    /// <summary>The override resolved to a concrete registered model.</summary>
    Resolved,

    /// <summary>The override named a provider that is not registered.</summary>
    UnknownProvider,

    /// <summary>The provider is known (or unqualified) but the model id is not registered.</summary>
    UnknownModel
}

/// <summary>
/// The outcome of preflighting a cron <c>Model</c> override against the model registry.
/// </summary>
/// <param name="Kind">The classification.</param>
/// <param name="Provider">The resolved provider when <see cref="CronModelPreflightKind.Resolved"/>; otherwise <see langword="null"/>.</param>
/// <param name="ModelId">The resolved model id when <see cref="CronModelPreflightKind.Resolved"/>; otherwise <see langword="null"/>.</param>
/// <param name="Reason">A bounded, redacted, human-readable reason when the override is a rejection; otherwise <see langword="null"/>.</param>
public readonly record struct CronModelPreflightResult(
    CronModelPreflightKind Kind,
    string? Provider,
    string? ModelId,
    string? Reason)
{
    /// <summary>
    /// Whether this result should block job creation/update and fail a run fast. Only the two
    /// "we positively know this model does not exist" kinds are rejections; a missing registry
    /// deliberately is not.
    /// </summary>
    public bool IsRejection =>
        Kind is CronModelPreflightKind.UnknownProvider or CronModelPreflightKind.UnknownModel;
}

/// <summary>
/// Preflight and classification for cron model overrides (#2373).
/// <para>
/// Cron accepts an arbitrary <c>model</c> override on every agent-prompt-shaped job. Without a
/// preflight, a typo'd or decommissioned model id produces a job that silently fails on <i>every
/// single fire</i>, surfacing only as an opaque provider error deep inside a run that nobody is
/// watching. This type is the single place that answers "does this override name a real model?"
/// so the cron tool can reject it at create/update time and the cron actions can record an
/// accurately classified reason on the run instead of a generic failure.
/// </para>
/// <para>
/// All operator-facing text produced here is bounded to <see cref="MaxReasonLength"/> and scrubbed
/// of key=value secret material, because it is persisted into the cron run record.
/// </para>
/// </summary>
public static class CronModelPreflight
{
    /// <summary>
    /// Maximum length of any reason/diagnostic string this type emits. Run records are a
    /// long-lived operator surface; an unbounded provider response body must never land there.
    /// </summary>
    public const int MaxReasonLength = 512;

    private static readonly Regex SecretAssignment = new(
        @"\b(api[_\-]?key|apikey|token|secret|password|passwd|authorization|auth|bearer)\b\s*[=:]\s*\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Classifies <paramref name="model"/> against <paramref name="registry"/>.
    /// </summary>
    /// <param name="registry">The model registry, or <see langword="null"/> when the host has none.</param>
    /// <param name="model">
    /// The raw override as authored on the job: either a bare <c>model-id</c> or a qualified
    /// <c>provider/model-id</c>. Provider aliases understood by <see cref="ModelRegistry"/>
    /// (for example <c>copilot</c>) are honoured.
    /// </param>
    /// <returns>The classification; never throws for malformed input.</returns>
    public static CronModelPreflightResult Resolve(ModelRegistry? registry, string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return new CronModelPreflightResult(CronModelPreflightKind.NotSpecified, null, null, null);

        var providers = registry?.GetProviders() ?? [];
        if (providers.Count == 0)
            return new CronModelPreflightResult(CronModelPreflightKind.RegistryUnavailable, null, null, null);

        var raw = model.Trim();
        var separator = raw.LastIndexOf('/');

        return separator > 0 && separator < raw.Length - 1
            ? ResolveQualified(registry!, providers, raw, raw[..separator], raw[(separator + 1)..])
            : ResolveBare(registry!, providers, raw);
    }

    /// <summary>
    /// Convenience wrapper for the run path: returns the classified rejection reason when the
    /// override cannot be honoured, or <see langword="null"/> when the run may proceed (resolved,
    /// unspecified, or unverifiable). Callers put the returned string straight onto the run record.
    /// </summary>
    /// <param name="registry">The model registry, or <see langword="null"/> when the host has none.</param>
    /// <param name="model">The raw override as authored on the job.</param>
    public static string? ClassifyRejection(ModelRegistry? registry, string? model)
    {
        var result = Resolve(registry, model);
        return result.IsRejection ? result.Reason : null;
    }

    /// <summary>
    /// Run-time guard for the cron actions: throws with the classified reason when the job's model
    /// override positively cannot be resolved, so the scheduler records that reason on the run
    /// record instead of an opaque provider error raised deep inside the agent turn. Silently
    /// falling back to the agent default is deliberately <b>not</b> done here - a scheduled job
    /// that quietly runs on a different model than the operator asked for is a worse failure than
    /// one that stops with an accurate reason.
    /// </summary>
    /// <param name="registry">The model registry, or <see langword="null"/> when the host has none.</param>
    /// <param name="model">The raw override as authored on the job.</param>
    /// <exception cref="InvalidOperationException">The override names an unknown provider or model.</exception>
    public static void EnsureResolvable(ModelRegistry? registry, string? model)
    {
        if (ClassifyRejection(registry, model) is { } reason)
            throw new InvalidOperationException(reason);
    }

    /// <summary>
    /// Bounds and redacts free-form provider diagnostic text before it is persisted onto a cron
    /// run record. Returns <see langword="null"/> for <see langword="null"/> input.
    /// </summary>
    /// <param name="diagnostic">The raw provider diagnostic / response text.</param>
    public static string? Summarize(string? diagnostic)
    {
        if (diagnostic is null)
            return null;

        var redacted = SecretAssignment.Replace(diagnostic, match => $"{match.Groups[1].Value}=[redacted]");
        redacted = Collapse(redacted);
        return Truncate(redacted);
    }

    private static CronModelPreflightResult ResolveQualified(
        ModelRegistry registry,
        IReadOnlyList<string> providers,
        string raw,
        string provider,
        string modelId)
    {
        // GetModel applies the registry's provider aliases (e.g. "copilot" -> "github-copilot"),
        // so a successful lookup is authoritative even when `provider` is not a canonical key.
        var resolved = registry.GetModel(provider, modelId);
        if (resolved is not null)
            return new CronModelPreflightResult(CronModelPreflightKind.Resolved, resolved.Provider, resolved.Id, null);

        // Distinguish "the provider does not exist" from "the provider exists but not this model":
        // GetModels also alias-resolves, so a non-empty list means the provider is real.
        var knownModels = registry.GetModels(provider);
        if (knownModels.Count == 0)
        {
            return new CronModelPreflightResult(
                CronModelPreflightKind.UnknownProvider,
                null,
                null,
                Truncate(
                    $"Cron model override '{raw}' names unknown provider '{provider}'. Known providers: ",
                    providers.Order(StringComparer.OrdinalIgnoreCase),
                    "."));
        }

        return new CronModelPreflightResult(
            CronModelPreflightKind.UnknownModel,
            null,
            null,
            Truncate(
                $"Cron model override '{raw}' is not a registered model for provider '{provider}'. Available: ",
                knownModels.Select(m => m.Id).Order(StringComparer.OrdinalIgnoreCase),
                "."));
    }

    private static CronModelPreflightResult ResolveBare(
        ModelRegistry registry,
        IReadOnlyList<string> providers,
        string raw)
    {
        foreach (var provider in providers.Order(StringComparer.OrdinalIgnoreCase))
        {
            var resolved = registry.GetModel(provider, raw);
            if (resolved is not null)
                return new CronModelPreflightResult(CronModelPreflightKind.Resolved, resolved.Provider, resolved.Id, null);
        }

        var qualified = providers
            .Order(StringComparer.OrdinalIgnoreCase)
            .SelectMany(provider => registry.GetModels(provider).Select(m => $"{provider}/{m.Id}"));

        return new CronModelPreflightResult(
            CronModelPreflightKind.UnknownModel,
            null,
            null,
            Truncate(
                $"Cron model override '{raw}' is not a registered model for any provider. Available: ",
                qualified,
                "."));
    }

    // Builds "<prefix><a>, <b>, ... (N more)<suffix>" while guaranteeing the whole string stays
    // within MaxReasonLength. The "(N more)" tail is what tells an operator the list was elided
    // rather than that the registry only holds a handful of models.
    private static string Truncate(string prefix, IEnumerable<string> items, string suffix)
    {
        var all = items.ToList();
        var builder = new StringBuilder(prefix);
        var written = 0;

        foreach (var item in all)
        {
            var candidate = written == 0 ? item : ", " + item;
            // Reserve room for the worst-case elision tail plus the suffix.
            var reserve = $", ... ({all.Count} more)".Length + suffix.Length;
            if (builder.Length + candidate.Length + reserve > MaxReasonLength)
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
        return Truncate(builder.ToString())!;
    }

    private static string? Truncate(string? value)
    {
        if (value is null || value.Length <= MaxReasonLength)
            return value;

        const string ellipsis = "...";
        return value[..(MaxReasonLength - ellipsis.Length)] + ellipsis;
    }

    // Provider diagnostics routinely arrive as multi-line bodies; collapsing whitespace keeps the
    // bounded budget spent on signal rather than newlines and indentation.
    private static string Collapse(string value)
        => Regex.Replace(value, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
}
