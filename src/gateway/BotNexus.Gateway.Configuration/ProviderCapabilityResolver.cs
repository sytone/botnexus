using BotNexus.Agent.Providers.Core.Registry;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Per-field resolution across the nested <c>chat</c> capability object and its deprecated flat
/// twins on <see cref="ProviderConfig"/> (#2854).
/// </summary>
/// <remarks>
/// <para>
/// The nested value wins whenever it is present; otherwise the flat value is used. Resolution is
/// per FIELD rather than per object on purpose: an operator migrating a live <c>config.json</c>
/// moves one key at a time, and an all-or-nothing rule would silently blank every field they had
/// not moved yet. Per-field fallback makes the half-migrated state a working state.
/// </para>
/// <para>
/// Every call site that reads a chat-shaped provider field should go through here rather than
/// touching <see cref="ProviderConfig.DefaultModel"/> and friends directly, so removing the flat
/// fields in a later release is a change to this one file.
/// </para>
/// </remarks>
public static class ProviderConfigCapabilityExtensions
{
    /// <summary>Resolves the chat API identifier, nested first.</summary>
    /// <param name="config">The provider configuration.</param>
    /// <returns>The chat api, or null when neither shape declares one.</returns>
    public static string? ResolveChatApi(this ProviderConfig config)
        => Coalesce(config?.Chat?.Api, config?.Api);

    /// <summary>Resolves the default chat model, nested first.</summary>
    /// <param name="config">The provider configuration.</param>
    /// <returns>The default chat model id, or null.</returns>
    public static string? ResolveChatDefaultModel(this ProviderConfig config)
        => Coalesce(config?.Chat?.DefaultModel, config?.DefaultModel);

    /// <summary>Resolves the chat model allowlist, nested first.</summary>
    /// <param name="config">The provider configuration.</param>
    /// <returns>The allowlist, or null meaning "all models".</returns>
    public static List<string>? ResolveChatModels(this ProviderConfig config)
        => config?.Chat?.Models ?? config?.Models;

    /// <summary>Resolves the explicit chat input modalities, nested first.</summary>
    /// <param name="config">The provider configuration.</param>
    /// <returns>The declared modalities, or null to infer them.</returns>
    public static List<string>? ResolveChatInput(this ProviderConfig config)
        => config?.Chat?.Input ?? config?.Input;

    /// <summary>Resolves the explicit reasoning declaration, nested first.</summary>
    /// <param name="config">The provider configuration.</param>
    /// <returns>The declaration, or null to infer it from the model family.</returns>
    public static bool? ResolveChatReasoning(this ProviderConfig config)
        => config?.Chat?.Reasoning ?? config?.Reasoning;

    /// <summary>Resolves the explicit extra-high thinking declaration, nested first.</summary>
    /// <param name="config">The provider configuration.</param>
    /// <returns>The declaration, or null to infer it from the model family.</returns>
    public static bool? ResolveChatSupportsExtraHighThinking(this ProviderConfig config)
        => config?.Chat?.SupportsExtraHighThinking ?? config?.SupportsExtraHighThinking;

    /// <summary>Resolves the explicit extended context-window declaration, nested first.</summary>
    /// <param name="config">The provider configuration.</param>
    /// <returns>The declaration, or null to infer it from the model family.</returns>
    public static bool? ResolveChatSupportsExtendedContextWindow(this ProviderConfig config)
        => config?.Chat?.SupportsExtendedContextWindow ?? config?.SupportsExtendedContextWindow;

    /// <summary>Resolves the declared chat context-window size, nested first.</summary>
    /// <param name="config">The provider configuration.</param>
    /// <returns>The size in tokens, or null for the platform default.</returns>
    public static int? ResolveChatContextWindow(this ProviderConfig config)
        => config?.Chat?.ContextWindow ?? config?.ContextWindow;

    private static string? Coalesce(string? nested, string? flat)
        => string.IsNullOrWhiteSpace(nested) ? flat : nested;
}

/// <summary>
/// Resolves the effective capability set for a provider as the UNION of what its code declares and
/// what its configuration declares, narrowed afterwards by <see cref="ProviderConfig.Enabled"/>
/// (#2854, per Jon's 2026-07-28 decision on #2500).
/// </summary>
/// <remarks>
/// <para>
/// The union is the point. A code-side registration (<see cref="ApiProviderRegistry.Register"/>,
/// #2853) can only speak for capabilities someone compiled in; a local OpenAI-compatible endpoint
/// that happens to serve embeddings has no code declaring so, and never will. Making the presence
/// of a capability OBJECT the config-side declaration lets an operator declare it without a code
/// change, while a provider whose code already declares embeddings keeps that declaration with no
/// config at all.
/// </para>
/// <para>
/// <b>Narrowing runs after the union, never inside it.</b> This matches the existing
/// <see cref="ConfigModelFilter"/> precedent, where the allowlist narrows a registry result rather
/// than constructing one. So <c>enabled: false</c> empties the set no matter which side declared
/// what -- a disabled provider is not partially available.
/// </para>
/// <para>
/// There is deliberately no config-side capability ALLOWLIST here. The issue puts a restricting
/// <c>capabilities</c> key explicitly out of scope: adding capabilities and removing them are
/// different semantics, and conflating them into one key is how a "declaration" quietly becomes a
/// permission check.
/// </para>
/// </remarks>
public static class ProviderCapabilityResolver
{
    /// <summary>
    /// Computes the effective capability set for one provider.
    /// </summary>
    /// <param name="config">
    /// The provider's configuration entry, or <see langword="null"/> when the provider is
    /// registered in code but absent from <c>config.json</c>. An absent entry declares nothing and
    /// narrows nothing, so the code declaration passes through unchanged.
    /// </param>
    /// <param name="codeDeclared">
    /// The capability set the registration declares (issue #2853), or <see langword="null"/> when
    /// the provider exists only in configuration.
    /// </param>
    /// <returns>
    /// The union of both declarations, or an empty set when the provider is explicitly disabled.
    /// </returns>
    public static IReadOnlySet<ProviderCapability> Resolve(
        ProviderConfig? config,
        IReadOnlySet<ProviderCapability>? codeDeclared)
    {
        // Narrowing first as a short-circuit: a disabled provider offers nothing, so there is
        // nothing to union. Expressed as an early return rather than a filter so the "removes
        // EVERY capability, from either side" rule is impossible to misread.
        if (config is { Enabled: false })
            return EmptySet;

        var effective = codeDeclared is null
            ? new HashSet<ProviderCapability>()
            : new HashSet<ProviderCapability>(codeDeclared);

        if (config?.Chat is not null)
            effective.Add(ProviderCapability.Chat);

        if (config?.Embeddings is not null)
            effective.Add(ProviderCapability.Embeddings);

        return effective;
    }

    private static readonly IReadOnlySet<ProviderCapability> EmptySet =
        new HashSet<ProviderCapability>();
}
